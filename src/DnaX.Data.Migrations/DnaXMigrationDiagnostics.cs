using System.Diagnostics;

namespace DnaX.Data.Migrations;

public static class DnaXMigrationDiagnostics
{
    public const string ActivitySourceName = "DnaX.Data.Migrations";

    internal static ActivitySource ActivitySource { get; } = new(ActivitySourceName);
}
