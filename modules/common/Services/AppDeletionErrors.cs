using Selfrun.Common.Models;

namespace Selfrun.Common.Services;

public static class AppDeletionErrors
{
    public const string ImportRequired = "legacy_provider_import_required";

    public static string ImportRequiredMessage(SelfrunApp app) =>
        $"Environment '{app.EnvironmentName ?? app.Slug}' ({app.Id}) requires provider ownership reconciliation. Use 'selfrun providers import-legacy --manifest <manifest.json>' to import its registered infrastructure into an organization provider account before deleting.";
}
