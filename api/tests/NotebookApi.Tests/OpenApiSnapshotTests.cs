using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NotebookApi.Tests;

/// <summary>
/// The Angular app's types are generated from api/openapi/v1.json. If the API changes and that file does not, the generated types
/// describe an API that no longer exists and the mistake only shows up in a browser. This test makes the file part of the contract:
/// it fails when the running API's OpenAPI document differs from the committed one.
/// To accept an intended change: set UPDATE_OPENAPI=1, run the tests once, then run `npm run gen:api` in web/ and commit both.
/// </summary>
public sealed class OpenApiSnapshotTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [Fact]
    public async Task The_committed_OpenAPI_snapshot_matches_the_API()
    {
        var client = factory.CreateClient();
        var live = Normalize(await client.GetStringAsync("/openapi/v1.json"));
        var path = SnapshotPath();

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, live);
        }

        Assert.True(File.Exists(path), $"No snapshot at {path}. Run once with UPDATE_OPENAPI=1.");
        Assert.True(
            Normalize(await File.ReadAllTextAsync(path)) == live,
            "The API's OpenAPI document no longer matches api/openapi/v1.json. If the change is intended: UPDATE_OPENAPI=1, rerun, then `npm run gen:api` in web/.");
    }

    // Line endings are normalised so a checkout with CRLF conversion does not cause a false failure.
    private static string Normalize(string json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, Pretty).Replace("\r\n", "\n") + "\n";

    private static string SnapshotPath([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "openapi", "v1.json"));
}
