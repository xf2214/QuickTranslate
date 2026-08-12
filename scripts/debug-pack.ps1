Param(
    [string]$PackedPath = "E:\翻译\assets\dictionaries\ecdict-lite.packed"
)

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

public static class EcdictPackedDebug
{
    public class Entry {
        public string Word = "";
        public string Phonetic = "";
        public string Translation = "";
    }

    public static int LoadPacked(string path, Dictionary<string, Entry> dest)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var br = new BinaryReader(fs, Encoding.UTF8))
        {

        var head4 = br.ReadBytes(4);
        if (head4.Length < 4) throw new InvalidDataException("Packed file too small");

        bool isFormatB = head4[0] == 0x31 && head4[1] == 0x44 &&
                         head4[2] == 0x43 && head4[3] == 0x45;

        if (!isFormatB)
        {
            var tail4 = br.ReadBytes(4);
            var expected = new byte[] { (byte)'E', (byte)'C', (byte)'D', (byte)'I', (byte)'C', (byte)'T', 0, 0 };
            if (!head4.Concat(tail4).SequenceEqual(expected))
            {
                throw new InvalidDataException("Bad packed magic (not an ECDICT packed file)");
            }
        }

        var version = br.ReadInt32();
        if (version < 1) throw new InvalidDataException("Unsupported version: " + version.ToString());

        var count = br.ReadInt32();
        if (count <= 0 || count > 5000000) throw new InvalidDataException("Invalid count: " + count.ToString());

        Console.WriteLine("  [DEBUG] isFormatB={0}, version={1}, count={2}", isFormatB, version, count);

        for (int i = 0; i < count; i++)
        {
            int wLen, pLen, tLen;
            try { wLen = br.ReadInt32(); }
            catch (EndOfStreamException ex) { throw new InvalidDataException("EOS at entry " + i.ToString() + " wLen", ex); }
            if (wLen <= 0 || wLen > 512) throw new InvalidDataException("Bad wLen=" + wLen.ToString() + " at entry " + i.ToString());
            byte[] wB = br.ReadBytes(wLen);
            if (wB.Length != wLen) throw new InvalidDataException("Short read for word at entry " + i.ToString());

            try { pLen = br.ReadInt32(); }
            catch (EndOfStreamException ex) { throw new InvalidDataException("EOS at entry " + i.ToString() + " pLen", ex); }
            if (pLen < 0 || pLen > 1024) throw new InvalidDataException("Bad pLen=" + pLen.ToString() + " at entry " + i.ToString() + ", word=" + Encoding.UTF8.GetString(wB));
            byte[] pB = pLen > 0 ? br.ReadBytes(pLen) : new byte[0];
            if (pB.Length != pLen) throw new InvalidDataException("Short read for phonetic at entry " + i.ToString());

            try { tLen = br.ReadInt32(); }
            catch (EndOfStreamException ex) { throw new InvalidDataException("EOS at entry " + i.ToString() + " tLen", ex); }
            if (tLen <= 0 || tLen > 1024 * 1024 * 16) throw new InvalidDataException("Bad tLen=" + tLen.ToString() + " at entry " + i.ToString() + ", word=" + Encoding.UTF8.GetString(wB));
            byte[] tB = br.ReadBytes(tLen);
            if (tB.Length != tLen) throw new InvalidDataException("Short read for translation at entry " + i.ToString());

            string w = Encoding.UTF8.GetString(wB);
            string p = pB.Length == 0 ? "" : Encoding.UTF8.GetString(pB);
            string t = Encoding.UTF8.GetString(tB);

            // Show first 3 entries, every 100,000th, last
            if (i < 3 || (i % 100000 == 0) || i == count - 1)
            {
                Console.WriteLine("  [entry {0}] word='{1}', phoneticLen={2}, translationLen={3}", i, w, pB.Length, tB.Length);
                Console.WriteLine("    translation preview: {0}", t.Length > 140 ? t.Substring(0, 140) + "..." : t);
            }

            dest[w] = new Entry { Word = w, Phonetic = p, Translation = t };
        }

        long left = fs.Length - fs.Position;
        Console.WriteLine("  [DEBUG] End loop. Remaining bytes: {0}", left);
        return dest.Count;
        }
    }
}
"@ -ReferencedAssemblies "System.Linq"

Write-Host "LoadPacked debug -> $PackedPath"
try {
    $dest = New-Object 'System.Collections.Generic.Dictionary[string, EcdictPackedDebug+Entry]' ([System.StringComparer]::OrdinalIgnoreCase)
    $cnt = [EcdictPackedDebug]::LoadPacked($PackedPath, $dest)
    Write-Host "OK -> loaded $cnt entries"
} catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
    if ($_.Exception.InnerException) { Write-Host "  INNER: $($_.Exception.InnerException)" -ForegroundColor DarkRed }
    Write-Host "  STACK: $($_.ScriptStackTrace)"
}
