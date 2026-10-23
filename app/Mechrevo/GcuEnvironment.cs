namespace YALM.Mechrevo;

internal static class GcuEnvironment
{
    private static readonly string[] Fields = ["CLIENT_ID", "USERNAME", "PASSWORD", "PORT"];

    public static string? Get(string field)
    {
        // Select one whole group. Never combine new and legacy credentials.
        bool useNewNames = Fields.Any(name => Environment.GetEnvironmentVariable("YALM_GCU_" + name) is not null);
        return Environment.GetEnvironmentVariable((useNewNames ? "YALM_GCU_" : "MHELPER_GCU_") + field);
    }
}
