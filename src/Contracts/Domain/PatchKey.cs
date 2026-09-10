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
    /// </summary>
    public string ToWorkflowId()
    {
        var composed = string.Join(
            Separator,
            Prefix,
            Sanitize(Namespace),
            Sanitize(WorkflowType),
            Sanitize(PatchId));

        if (composed.Length <= MaxWorkflowIdLength)
        {
            return composed;
        }

        var keep = MaxWorkflowIdLength - HashHexLength - 1;
        return $"{composed[..keep]}_{ShortHash(composed)}";
    }

    private static string Sanitize(string segment) =>
        UnsafeChars.Replace(segment ?? string.Empty, "_");

    private static string ShortHash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest)[..HashHexLength].ToLowerInvariant();
    }
}
