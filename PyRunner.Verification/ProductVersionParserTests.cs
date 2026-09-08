using PyRunner.Services;

internal static class ProductVersionParserTests
{
    public static int Run()
    {
        var failures = 0;
        void Verify(bool condition, string name)
        {
            if (condition) Console.WriteLine($"PASS: {name}");
            else
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {name}");
            }
        }

        Verify(ProductVersionParser.NormalizeInformationalVersion("1.0.0+abcdef") == "1.0.0",
            "informational version strips commit build metadata");
        Verify(ProductVersionParser.NormalizeInformationalVersion(null) is null &&
               ProductVersionParser.NormalizeInformationalVersion(string.Empty) is null &&
               ProductVersionParser.NormalizeInformationalVersion("invalid") is null,
            "missing or invalid informational version has no hard-coded fallback");
        Verify(ProductVersionParser.NormalizeInformationalVersion("١.٠.٠") is null,
            "local product version accepts ASCII digits only");
        return failures;
    }
}
