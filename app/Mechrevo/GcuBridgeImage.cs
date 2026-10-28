using System.Buffers.Binary;
using System.Security.Cryptography;

namespace YALM.Mechrevo;

/// <summary>
/// Restores the reviewed bridge's protected method section as data in memory.
/// The vendor module initializer is never loaded or executed. These parameters
/// describe the public file-protection arithmetic, not connection credentials.
/// </summary>
internal static class GcuBridgeImage
{
    private sealed record Profile(uint Z, uint X, uint C, uint V, uint SectionKey, string DecodedHash);

    private static readonly Dictionary<string, Profile> Profiles = new(StringComparer.Ordinal)
    {
        // 30-series 4.17.47.13 and F1 discrete 3.4.39.0 share protection parameters.
        ["2AA9B29606B292B15B3AF090D138B8455ACA69C5F620239B43F3F86375AE556A"] = new(
            1078105793, 2312454210, 330257521, 1038527970, 1939658490,
            "BDCEEE9E65173E446DF30C35163672794EB83B00E08185CA23954E009B08C2CD"),
        ["CF40A0F56D0AA48D7C4FE667A701C7FC212D323BC67376D63AFB003D5FB872DD"] = new(
            1078105793, 2312454210, 330257521, 1038527970, 1939658490,
            "5C7083CF07111FE9F7020F922E1FE7523583FCA2753B16463875D4E4ACF650E8"),
        // 5.56.1.24 and the reviewed 5.56.1.18 / 5.56.60.45 distributions.
        ["52B9E579986F84066E5FE5D2B2C4059110628EE4B005391CE0B2F7A6283437FD"] = new(
            3267396484, 437434542, 4083291987, 1936339304, 2149588376,
            "4BC790164677E06CDD295A22AACAA8AA9B77CE2CD7B537987D6EE91419F54722"),
        ["D7D1525F3D5F34A2A7D648C9ADD51B97DB31DA663416060CF2E3D9FC15F3725C"] = new(
            3267396484, 437434542, 4083291987, 1936339304, 2149588376,
            "A803CD574B9DEC610279D68CF03E5DC5BA851A07F3DF3A86E6E4F8F17E58592F"),
        ["1E2FBA7FB6C91221EDD69B4D97B82A2650D4522184EF81C7283F27E172695132"] = new(
            2512773533, 870310000, 1970822959, 1530329601, 595240232,
            "F8B0B9092CA88520D64BC920A365485025137C2CA9B9DBFD3142A794531D93E7"),
        ["E4924979B357FEF9591F3E122D59C5B40EA2E7C2F0829E1E9C69BAC01CC4004E"] = new(
            1383541747, 3681050456, 3205946409, 1401446915, 4178948480,
            "252393C7C634C5DFFC8AB0D3D95687383BCC33D2595C81B528DF066A8C538E96"),
        ["8EE0B5E039BC3F61AD897C210B822FE68412ED655257FB07DC5095CAD9FF1CBA"] = new(
            87932803, 2995531116, 1091507981, 819688709, 3661295304,
            "EA90E53492E4568E36014463F786DF54038AB11D32F0A49BE1951EC05B38CC90"),
        ["3FFF9FF854CFFA0FA290D2B67203917A42C394EC204402C23401745EC9924C5B"] = new(
            1561329104, 2844982693, 3513058306, 1310098496, 1324691361,
            "8ABC60F56A057F0139C0C97F4C9829E67AF19E890806D0B6CBC55923DB5AC0D5")
    };

    internal static bool IsProtected(string hash) => !string.IsNullOrEmpty(hash) && Profiles.ContainsKey(hash);

    internal static byte[]? ReadDecrypted(string hash, byte[] original)
    {
        // Also reject a stale hash supplied for bytes changed after discovery.
        if (string.IsNullOrEmpty(hash) || !Profiles.TryGetValue(hash, out Profile? profile) || original is null ||
            original.Length is < 64 or > 16 * 1024 * 1024 ||
            Convert.ToHexString(SHA256.HashData(original)) != hash)
            return null;

        ReadOnlySpan<byte> source = original;
        if (U16(source, 0) != 0x5a4d) return null;
        uint peOffset = U32(source, 0x3c);
        if (!Contains(source.Length, peOffset, 24) || U32(source, (int)peOffset) != 0x00004550) return null;
        int pe = (int)peOffset;
        ushort count = U16(source, pe + 6);
        ushort optionalSize = U16(source, pe + 20);
        if (count is 0 or > 96 || optionalSize < 96) return null;
        uint tableOffset = peOffset + 24u + optionalSize;
        if (!Contains(source.Length, tableOffset, (uint)count * 40)) return null;

        uint z = profile.Z, x = profile.X, c = profile.C, v = profile.V;
        int targetOffset = -1, targetSize = 0;
        for (int i = 0; i < count; i++)
        {
            int section = (int)tableOffset + i * 40;
            uint name = unchecked(U32(source, section) * U32(source, section + 4));
            uint virtualSize = U32(source, section + 8);
            uint rawSize = U32(source, section + 16);
            uint rawOffset = U32(source, section + 20);
            if (!Contains(source.Length, rawOffset, rawSize)) return null;

            if (name == profile.SectionKey)
            {
                if (targetOffset >= 0 || virtualSize < 4 || virtualSize > rawSize) return null;
                targetOffset = (int)rawOffset;
                targetSize = (int)virtualSize & ~3;
            }
            else if (name != 0)
            {
                int end = (int)rawOffset + ((int)rawSize & ~3);
                for (int offset = (int)rawOffset; offset < end; offset += 4)
                {
                    uint next = unchecked((z ^ U32(source, offset)) + x + c * v);
                    z = x;
                    // The reviewed initializer overwrites x twice; c stays unchanged.
                    x = v;
                    v = next;
                }
            }
        }
        if (targetOffset < 0) return null;

        Span<uint> keys = stackalloc uint[16];
        Span<uint> otherKeys = stackalloc uint[16];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = v;
            otherKeys[i] = x;
            z = (x >> 5) | (x << 27);
            x = (c >> 3) | (c << 29);
            c = (v >> 7) | (v << 25);
            v = (z >> 11) | (z << 21);
        }
        for (int i = 0; i < keys.Length; i++)
            keys[i] = (i % 3) switch
            {
                0 => keys[i] ^ otherKeys[i],
                1 => unchecked(keys[i] * otherKeys[i]),
                _ => unchecked(keys[i] + otherKeys[i])
            };

        byte[] image = (byte[])original.Clone();
        for (int i = 0; i < targetSize / 4; i++)
        {
            int offset = targetOffset + i * 4;
            uint value = U32(source, offset) ^ keys[i & 15];
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4), value);
            keys[i & 15] = unchecked((keys[i & 15] ^ value) + 1035675673);
        }
        // A second fingerprint binds both the reviewed algorithm and its output.
        return Convert.ToHexString(SHA256.HashData(image)) == profile.DecodedHash ? image : null;
    }

    private static bool Contains(int length, uint offset, uint size) => offset <= length && size <= (uint)length - offset;
    private static ushort U16(ReadOnlySpan<byte> source, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> source, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));
}
