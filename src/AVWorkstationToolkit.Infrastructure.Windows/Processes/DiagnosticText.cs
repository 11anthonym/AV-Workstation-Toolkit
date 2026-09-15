using System.Text;
using System.Text.RegularExpressions;
using AVWorkstationToolkit.Application.Diagnostics;

namespace AVWorkstationToolkit.Infrastructure.Windows.Processes;

internal static partial class DiagnosticText
{
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AnsiPattern();

    [GeneratedRegex(@"(?i)(password|passwd|token|api[-_ ]?key|secret)\s*[:=]\s*\S+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SecretPattern();

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var withoutAnsi = AnsiPattern().Replace(value, string.Empty);
        var builder = new StringBuilder(withoutAnsi.Length);
        foreach (var character in withoutAnsi)
        {
            if (character is '\r' or '\n' or '\t' || (!char.IsControl(character) && character != '\u007f'))
                builder.Append(character);
        }
        // Redact quoted credentials/bearer tokens before the token-based fallback
        // so a quoted value containing spaces cannot leak its remaining words.
        return SecretPattern().Replace(DiagnosticsRedactor.Sanitize(builder.ToString()), "$1=[REDACTED]").TrimEnd();
    }
}
