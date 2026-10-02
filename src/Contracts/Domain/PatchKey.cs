using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Contracts.Domain;

/// <summary>
/// Identidad de un patch en juego: el namespace donde vive, el tipo de workflow que lo
/// declara y el <c>patchId</c> pasado a <c>Workflow.Patched</c>. Es la clave por la que el
/// spec 05 va a crear un entity workflow por patch.
/// </summary>
public sealed record PatchKey(string Namespace, string WorkflowType, string PatchId)
{
    private const string Prefix = "patch-state";
    private const string Separator = "::";
    private const int MaxWorkflowIdLength = 200;
    private const int HashHexLength = 8;

    private static readonly Regex UnsafeChars = new("[^A-Za-z0-9._-]", RegexOptions.Compiled);

    /// <summary>
    /// Id determinístico y saneado para usar como <c>WorkflowId</c> del entity workflow del
    /// patch. Formato <c>patch-state::{Namespace}::{WorkflowType}::{PatchId}</c>, con cada
    /// segmento saneado (todo carácter fuera de <c>[A-Za-z0-9._-]</c> pasa a <c>_</c>, sin
    /// cambiar el casing). Si el id supera los 200 caracteres se trunca a 191 y se le
    /// concatena <c>_</c> más los primeros 8 hex del SHA-256 del id sin truncar.
    /// <para>
    /// Si el saneado cambió algún segmento (<c>"a b"</c> y <c>"a_b"</c> darían el mismo id), se
    /// agrega siempre <c>_</c> más los 8 hex del SHA-256 de los segmentos crudos, para que dos
    /// keys distintas no compartan entity (spec 16, B-2). Las keys que solo usan
    /// <c>[A-Za-z0-9._-]</c> conservan exactamente el id de antes.
    /// </para>
    /// </summary>
    public string ToWorkflowId()
    {
        var sanitized = new[] { Sanitize(Namespace), Sanitize(WorkflowType), Sanitize(PatchId) };
        var raw = new[] { Namespace ?? string.Empty, WorkflowType ?? string.Empty, PatchId ?? string.Empty };
        var changed = !sanitized.SequenceEqual(raw);

        var composed = string.Join(Separator, new[] { Prefix }.Concat(sanitized));

        if (!changed && composed.Length <= MaxWorkflowIdLength)
        {
            return composed;
        }

        // Sin cambios, el hash es el del compuesto (igual que antes). Con cambios se hashean los
        // segmentos crudos separados por U+001F, que no puede aparecer en un segmento "::"-ambiguo.
        var hashInput = changed ? string.Join('\u001f', raw) : composed;
        var keep = Math.Min(composed.Length, MaxWorkflowIdLength - HashHexLength - 1);
        return $"{composed[..keep]}_{ShortHash(hashInput)}";
    }

    private static string Sanitize(string segment) =>
        UnsafeChars.Replace(segment ?? string.Empty, "_");

    private static string ShortHash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest)[..HashHexLength].ToLowerInvariant();
    }
}
