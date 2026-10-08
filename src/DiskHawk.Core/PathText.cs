using System;
using System.Text;

namespace DiskHawk.Core
{
    /// <summary>
    /// Security helpers for file/folder names and paths. Scan results (reports from remote machines,
    /// raw $MFT names) are treated as untrusted: names containing control characters, path separators or
    /// characters invalid on Windows are replaced with '?'. Because '?' is invalid for deletion (Classify),
    /// such a name can never be a deletion target; it is only displayed.
    /// </summary>
    public static class PathText
    {
        public const char Replacement = '?';

        /// <summary>Is the character invalid in a single file/folder name?</summary>
        public static bool IsBadNameChar(char c)
        {
            return c < 0x20 || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|';
        }

        /// <summary>Is the character invalid inside a path (separators excluded)?</summary>
        public static bool IsBadPathChar(char c)
        {
            return c < 0x20 || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|';
        }

        public static bool HasControlChars(string s)
        {
            if (s == null) return false;
            foreach (var c in s) if (c < 0x20) return true;
            return false;
        }

        /// <summary>Single name: invalid characters, "." and ".." -> '?'. Returns the same instance if unchanged (fast path).</summary>
        public static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return Replacement.ToString();
            if (name == "." || name == "..") return new string(Replacement, name.Length);
            int i = 0;
            for (; i < name.Length; i++) if (IsBadNameChar(name[i])) break;
            if (i == name.Length) return name;
            var sb = new StringBuilder(name);
            for (; i < sb.Length; i++) if (IsBadNameChar(sb[i])) sb[i] = Replacement;
            return sb.ToString();
        }

        /// <summary>Full path: separators and the drive colon ("X:" or "\\?\X:") are kept, other invalid characters become '?'.</summary>
        public static string SafePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path ?? "";
            int keepQ = path.StartsWith(@"\\?\") ? 2 : -1;              // the '?' of the \\?\ prefix
            int colon = keepQ > 0 ? 5 : 1;                              // the ':' after the drive letter
            var sb = new StringBuilder(path);
            bool changed = false;
            for (int i = 0; i < sb.Length; i++)
            {
                char c = sb[i];
                if (i == keepQ || (c == ':' && i == colon)) continue;
                if (IsBadPathChar(c) || c == ':') { sb[i] = Replacement; changed = true; }
            }
            return changed ? sb.ToString() : path;
        }

        /// <summary>Free text (machine name, error message): control characters become spaces, length is limited.</summary>
        public static string SafeText(string s, int maxLen = 4096)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.Length > maxLen) s = s.Substring(0, maxLen);
            if (!HasControlChars(s)) return s;
            var sb = new StringBuilder(s);
            for (int i = 0; i < sb.Length; i++) if (sb[i] < 0x20) sb[i] = ' ';
            return sb.ToString();
        }

        /// <summary>Local drive path starting with "X:\"?</summary>
        public static bool IsDrivePath(string p)
        {
            return p != null && p.Length >= 3 && IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
        }

        /// <summary>"\\server\share..." form (excluding \\?\ and \\.\).</summary>
        public static bool IsUncPath(string p)
        {
            return p != null && p.Length > 2 && p[0] == '\\' && p[1] == '\\' && p[2] != '?' && p[2] != '.';
        }

        private static bool IsAsciiLetter(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'); }

        /// <summary>
        /// Machine name: rejects characters that would break a UNC path, the WMI address or a folder name
        /// (\ / : * ? " &lt; &gt; | ; , whitespace, control characters, "..", leading/trailing dot).
        /// Non-ASCII letters and the other characters NetBIOS allows are accepted.
        /// </summary>
        public static bool IsValidMachineName(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 253) return false;
            if (s[0] == '.' || s[0] == '-' || s[s.Length - 1] == '.') return false;
            if (s.Contains("..")) return false;
            foreach (var c in s)
                if (c <= 0x20 || char.IsWhiteSpace(c) || "\\/:*?\"<>|;,@=+[]%".IndexOf(c) >= 0) return false;
            return true;
        }

        /// <summary>
        /// CSV / Excel formula injection: cells starting with '=', '+', '-', '@', tab or carriage return
        /// can run as formulas in Excel. A ' is prepended (OWASP recommendation).
        /// </summary>
        public static string CsvSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            char c = s[0];
            if (c == '=' || c == '+' || c == '-' || c == '@' || c == '\t' || c == '\r' || c == '\uFF1D' || c == '\uFF0B' || c == '\uFF0D' || c == '\uFF20')
                return "'" + s;
            return s;
        }
    }
}
