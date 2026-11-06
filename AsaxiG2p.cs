using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OpenUtau.Api;

namespace Asaxi {
    /// <summary>
    /// Rule-based grapheme-to-phoneme for Asaxi. Every word is phonemised from its spelling;
    /// there is no word list, so a word nobody has ever written works the moment it is typed.
    ///
    /// The rules are documented in the Lozenge-T training package (a separate repository) and
    /// implemented twice on purpose: this class, and <c>asaxi_g2p.py</c> there. The two are held
    /// together by <c>tests/asa_conformance.tsv</c>, which that package generates from the
    /// Python side and <c>tests/Conformance</c> checks this class against.
    ///
    /// Output is BARE tokens. OpenUtau's <c>DiffSingerG2pPhonemizer</c> wraps this in a
    /// <c>G2pRemapper</c> driven by <c>GetLangCode()</c>, which is what turns <c>kx</c> into
    /// <c>asa/kx</c> for a multi-language bank.
    /// </summary>
    public class AsaxiG2p : IG2p {
        // ------------------------------------------------------------------ graphemes
        static readonly string[] Multi = {
            "nŋ", "nn", "mm", "rr", "ch", "sh", "dh", "jh", "zh", "th", "dz", "si", "ni",
        };
        static readonly string Single =
            "aáăåèëěỏőùůý" + "eoiu" + "ŋŕńś" + "'" + "xcjbfghklmprstvwxyz";

        static readonly string[] Graphemes = Multi
            .Concat(Single.Select(c => c.ToString()))
            .Distinct()
            .OrderByDescending(g => g.Length)
            .ThenBy(g => g, StringComparer.Ordinal)
            .ToArray();

        /// <summary>grapheme -> bare token.</summary>
        static readonly Dictionary<string, string> Base = new Dictionary<string, string> {
            // vowels: the diacritics are the diphthongs
            { "a", "a" }, { "á", "ao" }, { "å", "aw" }, { "ă", "ay" }, { "e", "e" },
            { "ë", "ey" }, { "è", "ax" }, { "ě", "er" }, { "i", "i" }, { "ý", "ih" },
            { "o", "o" }, { "ỏ", "ow" }, { "ő", "oy" }, { "u", "u" }, { "ù", "u" },
            { "ů", "uw" },
            // consonants
            { "b", "b" }, { "ch", "ch" }, { "d", "d" }, { "dh", "dh" }, { "f", "f" },
            { "g", "g" }, { "h", "h" }, { "j", "j" }, { "jh", "jh" }, { "k", "k" },
            { "l", "l" }, { "m", "m" }, { "n", "n" }, { "ŋ", "ng" }, { "ń", "ny" },
            { "ni", "ny" }, { "p", "p" }, { "r", "r" }, { "s", "s" }, { "sh", "sh" },
            { "ś", "S" }, { "si", "S" }, { "t", "t" }, { "th", "th" }, { "v", "v" },
            { "w", "w" }, { "x", "hh" }, { "z", "z" }, { "zh", "zh" }, { "c", "ts" },
            { "dz", "dz" }, { "ŕ", "dx" }, { "'", "q" },
            // syllabic nuclei
            { "mm", "mm" }, { "nn", "nn" }, { "nŋ", "nng" }, { "rr", "rr" },
        };

        /// <summary>Digraphs that also emit the following <c>i</c>.</summary>
        static readonly HashSet<string> DigraphWithI = new HashSet<string> { "si", "ni" };

        static readonly HashSet<string> VowelGraphemes = new HashSet<string> {
            "a", "e", "i", "o", "u", "á", "å", "ă", "è", "ë", "ě", "ỏ", "ő", "ù", "ů", "ý",
        };
        static readonly HashSet<string> Syllabic = new HashSet<string> { "mm", "nn", "nŋ", "rr" };

        /// <summary>Consonants that can carry a marker.</summary>
        static readonly HashSet<string> ConsonantG = new HashSet<string> {
            "b", "ch", "d", "dh", "f", "g", "h", "jh", "k", "l", "m", "n", "ŋ", "p",
            "r", "s", "sh", "ś", "t", "th", "v", "z", "zh", "c", "dz", "ń", "ŕ",
        };
        /// <summary>Doubling one of these closes the airflow: it becomes <c>cl</c> + the stop.</summary>
        static readonly HashSet<string> StopG = new HashSet<string> {
            "p", "t", "k", "b", "d", "g", "ch", "c", "dz", "jh",
        };

        static readonly HashSet<string> CanTakeW = new HashSet<string>(ConsonantG.Where(c => c != "r"));
        static readonly HashSet<string> CanTakeJ = new HashSet<string>(
            ConsonantG.Where(c => c != "r" && c != "n" && c != "s" && c != "z"
                                  && c != "ń" && c != "ś" && c != "zh"));
        static readonly HashSet<string> CanTakeX = new HashSet<string> { "p", "t", "k", "ch", "c" };

        /// <summary>
        /// The fused imperative: <c>-wë</c> is <c>ů</c> + <c>hè</c>, so its <c>w</c> descends
        /// from a vowel and is a syllable onset, never labialisation. The vault spells the
        /// pronunciation out for <c>shivënwë</c>: /ɕi.veɪn.weː/.
        /// </summary>
        static readonly Dictionary<string, string[]> Overrides = new Dictionary<string, string[]> {
            { "shivënwë", new[] { "sh", "i", "v", "ey", "n", "w", "ey" } },
            { "nåhèshivënwë", new[] { "n", "aw", "h", "ax", "sh", "i", "v", "ey", "n", "w", "ey" } },
            { "shěsonwë", new[] { "sh", "er", "s", "o", "n", "w", "ey" } },
            { "exåshwë", new[] { "e", "hh", "aw", "sh", "w", "ey" } },
        };

        // ------------------------------------------------------------------ inventory
        public static readonly string[] Vowels = {
            "a", "ao", "aw", "ay", "ax", "e", "er", "ey", "i", "ih",
            "o", "ow", "oy", "u", "uw", "yj",
        };
        /// <summary>Syllabic nuclei count as vowels for placement.</summary>
        public static readonly string[] Nuclei = { "mm", "nn", "nng", "xn", "rr" };

        public static readonly string[] Consonants = new[] {
            "b", "ch", "d", "dh", "dx", "dz", "f", "g", "h", "hh", "j", "jh", "k", "l",
            "m", "n", "ng", "ny", "p", "q", "r", "s", "S", "sh", "t", "th", "ts", "v",
            "w", "z", "zh", "cl",
        }.Concat(
            CanTakeW.Select(c => Base[c] + "w")
        ).Concat(
            CanTakeJ.Select(c => Base[c] + "y")
        ).Concat(
            CanTakeX.Select(c => Base[c] + "x")
        ).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();

        static readonly HashSet<string> VowelSet = new HashSet<string>(Vowels.Concat(Nuclei));
        static readonly HashSet<string> AllSymbols =
            new HashSet<string>(Vowels.Concat(Nuclei).Concat(Consonants));

        /// <summary>The glide phones: <c>j</c> and <c>w</c> are full onsets in Asaxi.</summary>
        static readonly HashSet<string> Glides = new HashSet<string> { "j", "w", "yj" };

        /// <summary>
        /// Pause phones. <c>SP</c> is silence and <c>AP</c> is a breath, and both are
        /// language-neutral bank symbols rather than Asaxi phonemes, so they are not part of
        /// the 103-token inventory and never receive the <c>asa/</c> prefix.
        ///
        /// They must still be RECOGNISED here, or a lyric of <c>AP</c> would be phonemised as
        /// the letters *a* + *p* and a lyric of <c>SP</c> as *s* + *p* — which is exactly the
        /// wrong thing to put on a note the singer is meant to breathe on.
        /// </summary>
        static readonly HashSet<string> Pauses = new HashSet<string> { "SP", "AP" };

        static bool IsPause(string value) =>
            value != null && value.Length == 2 && Pauses.Contains(value.ToUpperInvariant());

        // ------------------------------------------------------------------ IG2p
        public bool IsValidSymbol(string symbol) =>
            symbol != null && (IsPause(symbol) || AllSymbols.Contains(symbol));

        // the host registers SP/AP as vowels for placement purposes; match that
        public bool IsVowel(string symbol) =>
            symbol != null && (IsPause(symbol) || VowelSet.Contains(symbol));

        public bool IsGlide(string symbol) => symbol != null && Glides.Contains(symbol);

        public string[] UnpackHint(string hint, char separator = ' ') {
            if (string.IsNullOrWhiteSpace(hint)) {
                return null;
            }
            var result = hint
                .Split(separator)
                .Where(s => AllSymbols.Contains(s) || IsPause(s))
                .ToArray();
            return result.Length == 0 ? null : result;
        }

        public string[] Query(string grapheme) {
            if (string.IsNullOrWhiteSpace(grapheme)) {
                return null;
            }
            var raw = grapheme.Trim();
            if (IsPause(raw)) {
                return new[] { raw.ToUpperInvariant() };
            }
            var key = raw.ToLower(new CultureInfo("en-US"));
            if (Overrides.TryGetValue(key, out var overridden)) {
                return (string[])overridden.Clone();
            }
            var tokens = Phonemise(key);
            return tokens.Count == 0 ? null : tokens.ToArray();
        }

        // ------------------------------------------------------------------ the rules
        /// <summary>Longest-match grapheme split; anything with no rule becomes a single char.</summary>
        internal static List<string> Tokenize(string word) {
            var result = new List<string>();
            var i = 0;
            while (i < word.Length) {
                string match = null;
                foreach (var g in Graphemes) {
                    if (i + g.Length <= word.Length
                        && string.CompareOrdinal(word, i, g, 0, g.Length) == 0) {
                        match = g;
                        break;
                    }
                }
                if (match == null) {
                    result.Add(word[i].ToString());
                    i += 1;
                } else {
                    result.Add(match);
                    i += match.Length;
                }
            }
            return result;
        }

        static bool IsLetterOrApostrophe(string g) =>
            Base.ContainsKey(g) || g == "'";

        internal static List<string> Phonemise(string word) {
            var graphemes = Tokenize(word)
                .Where(IsLetterOrApostrophe)
                .ToList();

            var result = new List<string>();
            var i = 0;
            while (i < graphemes.Count) {
                var t = graphemes[i];
                var next = i + 1 < graphemes.Count ? graphemes[i + 1] : null;

                // ýj before a consonant or at the end: one mora, one token
                if (t == "ý" && next == "j"
                    && (i + 2 >= graphemes.Count || !VowelGraphemes.Contains(graphemes[i + 2]))) {
                    result.Add("yj");
                    i += 2;
                    continue;
                }

                // gemination; the syllabic nuclei are already single tokens
                if (next == t && !Syllabic.Contains(t)) {
                    if (StopG.Contains(t)) {
                        result.Add("cl");
                        result.Add(Base[t]);
                    } else if (DigraphWithI.Contains(t)) {
                        result.Add(Base[t]);
                    } else {
                        var p = Base[t];
                        result.Add(p);
                        result.Add(p);
                    }
                    i += 2;
                    continue;
                }

                // markers
                if (next != null && (next == "j" || next == "w" || next == "x")
                    && ConsonantG.Contains(t)) {
                    if (next == "j" && CanTakeJ.Contains(t)) {
                        result.Add(Base[t] + "y");
                        i += 2;
                        continue;
                    }
                    if (next == "w" && CanTakeW.Contains(t)) {
                        result.Add(Base[t] + "w");
                        i += 2;
                        continue;
                    }
                    if (next == "x" && CanTakeX.Contains(t)) {
                        result.Add(Base[t] + "x");
                        i += 2;
                        continue;
                    }
                    // not a fusion: both graphemes are emitted separately below
                }

                var tok = Base[t];
                result.Add(tok);
                if (DigraphWithI.Contains(t)) {
                    result.Add("i");
                }
                i += 1;
            }

            // a word-final syllabic nn is the uvular coda nucleus
            if (result.Count > 0 && result[result.Count - 1] == "nn") {
                result[result.Count - 1] = "xn";
            }
            return result;
        }
    }
}
