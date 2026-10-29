using System.ComponentModel.DataAnnotations;

namespace NotebookApi.Infrastructure;

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>SQLite file. A relative path is resolved against the application's content root, not the working directory, so <c>dotnet run</c> and a published build use the same file.</summary>
    [Required]
    public string Path { get; init; } = "App_Data/notebook.db";
}
