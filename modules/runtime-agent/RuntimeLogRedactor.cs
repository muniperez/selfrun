using Selfrun.Common.Redaction;

namespace Selfrun.RuntimeAgent;

public static class RuntimeLogRedactor
{
    public static string Redact(string value) => LogRedactor.Redact(value);
}
