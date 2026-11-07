using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        /// <summary>The bank-name prefix every Asaxi token carries.</summary>
        public const string Prefix = "asa/";

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
        /// Cluster token -> the two phones it is realised as.
        ///
        /// <para>Labialisation and palatalisation split only when the bank has no such cluster:
        /// <c>kw</c> becomes <c>k</c> + <c>w</c>, <c>ky</c> becomes <c>k</c> + <c>j</c>.</para>
        ///
        /// <para>Aspiration splits <b>always</b>. An aspirated stop is a stop plus a release, and
        /// no bank of this family has a trained aspirated series, so <c>kx</c> is realised as
        /// <c>k</c> + <c>hh</c> — the aspiration is carried by the <c>hh</c> token, whose
        /// placeholder on a 2.5-family bank is <c>en/hh</c>.</para>
        ///
        /// <para>Base tokens that merely look like clusters never split: <c>ny</c> is /ɲ/ and
        /// <c>hy</c> is /ç/, each one phoneme.</para>
        /// </summary>
        static readonly Dictionary<string, (string Stem, string Glide, bool AlwaysSplit)>
            Splittable = BuildSplittable();

        static Dictionary<string, (string, string, bool)> BuildSplittable() {
            var table = new Dictionary<string, (string, string, bool)>();
            foreach (var g in CanTakeW) {
                table[Base[g] + "w"] = (Base[g], "w", false);
            }
            foreach (var g in CanTakeJ) {
                table[Base[g] + "y"] = (Base[g], "j", false);
            }
            foreach (var g in CanTakeX) {
                table[Base[g] + "x"] = (Base[g], "hh", true);
            }
            table.Remove("hy");   // /ç/ is one phoneme, not h + j
            return table;
        }

        /// <summary>
        /// If <paramref name="token"/> is a labialised or palatalised cluster, the base token and
        /// the glide it can be spelled with on a bank that has no such cluster.
        /// </summary>
        public static bool TrySplitCluster(string token, out string stem, out string glide) {
            stem = glide = null;
            if (string.IsNullOrEmpty(token) || !Splittable.TryGetValue(token, out var parts)) {
                return false;
            }
            stem = parts.Stem;
            glide = parts.Glide;
            return true;
        }

        /// <summary>
        /// Prefixes a bank may put in front of a phone name, tried in this order after the
        /// unprefixed form. A multi-language DiffSinger bank usually names every phone
        /// `<lang>/<phone>` and has no bare names at all, so an Asaxi token has to be looked up
        /// under the language whose inventory contains it.
        /// </summary>
        public static readonly string[] BankPrefixes = {
            "", "en/", "ja/", "pl/", "zh/", "es/", "ko/", "tg/",
        };

        /// <summary>
        /// The spellings to try, in order, when a bank has no `asa/&lt;token&gt;`. Only the tokens
        /// whose Asaxi spelling is NOT the spelling another language would use need an entry:
        /// `j` is the glide (banks call it `y`), `S` is /ɕ/ (`sh` or `S`), `rr` is the rhotic vowel
        /// (`er` before the Japanese flap `rr`), and so on. Everything else falls through to its
        /// own name.
        /// </summary>
        static readonly Dictionary<string, string[]> FallbackAliases = new Dictionary<string, string[]> {
            // Names that mean a DIFFERENT phone in another language. Values are the acceptable
            // names in order; a qualified name is tried verbatim, a plain one bare only, so a
            // bank can never be satisfied by a phone that merely shares the spelling.
            // IPA per EN-/PL-/JA-phonemiser-IPA-documentation.md.
            { "j", new[] { "en/y", "ja/y", "y" } },            // ja/j is /dʑ/
            { "S", new[] { "pl/S", "ja/sh", "zh/x", "S", "sh" } },   // en/sh is /ʃ/
            { "h", new[] { "pl/h", "zh/h", "h" } },            // en/h is /h/, Asaxi is /x/
            { "f", new[] { "en/f", "pl/f", "zh/f", "f" } },    // ja/f is /ɸ/
            { "ch", new[] { "en/ch", "ch" } },                 // ja/ch is /tɕ/
            { "ts", new[] { "ja/ts", "pl/c", "en/ts", "ts" } },
            { "dz", new[] { "ja/dz", "en/dz", "dz" } },
            { "ny", new[] { "ja/ny", "pl/N", "ny", "N" } },
            { "dx", new[] { "en/dx", "ja/r", "dx", "r" } },
            { "r", new[] { "en/r", "zh/r", "r" } },            // ja/r is a flap
            { "rr", new[] { "en/er", "er", "ja/rr", "rr" } },
            { "hh", new[] { "en/hh", "hh", "h", "H" } },
            { "hy", new[] { "ja/hy", "hy" } },
            { "v", new[] { "en/v", "pl/w", "v" } },
            { "z", new[] { "en/z", "pl/z", "ja/z", "z" } },    // zh/z is /ts/
            { "e", new[] { "pl/e", "ja/e", "en/e", "e", "eh", "ey" } },  // zh/e is /ɤ/
            { "i", new[] { "ja/i", "en/iy", "pl/i", "i", "iy" } },
            { "o", new[] { "ja/o", "en/o", "o", "oh", "ow" } },
            { "u", new[] { "ja/u", "u", "uu", "uw" } },        // en/uw is /u/, Asaxi is /ɯ/
            { "yj", new[] { "en/iy", "iy", "ih" } },
            // q is the one name that is a DIFFERENT phone in Mandarin (/tɕʰ/); the nuclei
            // and `cl` are not ambiguous and stay on the generic ladder above.
            { "q", new[] { "en/q", "ja/q", "q" } },            // NEVER zh/q (/tɕʰ/)
        };

        /// <summary>Spellings to try for a token, most specific first.</summary>
        public static string[] FallbackNames(string token) =>
            FallbackAliases.TryGetValue(token, out var names) ? names : new[] { token };

        /// <summary>
        /// The bank's name for a phonetic-hint symbol, or null when it has none. Accepts the
        /// symbol as a bank name, as an Asaxi token, or as any of the spellings this module uses
        /// for a token (`y` for `j`, `N` for `ny`, `sil`/`inh` for the pauses).
        /// </summary>
        public static string ResolveHintSymbol(string symbol, Func<string, bool> singerHas) {
            if (string.IsNullOrEmpty(symbol)) {
                return null;
            }
            symbol = symbol.Trim();
            if (singerHas(symbol)) {
                return symbol;
            }
            if (singerHas(Prefix + symbol)) {
                return Prefix + symbol;
            }
            var asToken = ResolveBankName(Prefix + symbol, singerHas);
            if (asToken != null) {
                return asToken;
            }
            if (symbol == "sil" && singerHas("SP")) {
                return "SP";
            }
            if (symbol == "inh" && singerHas("AP")) {
                return "AP";
            }
            // an alternative spelling of a token: `y` belongs to `j`, `N` to `ny`, ...
            foreach (var pair in FallbackAliases) {
                foreach (var name in pair.Value) {
                    var leaf = name.Substring(name.LastIndexOf('/') + 1);
                    if (leaf == symbol) {
                        var resolved = ResolveBankName(Prefix + pair.Key, singerHas);
                        if (resolved != null) {
                            return resolved;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// The bank's name for an Asaxi token, or null when it has none. Tries the token's own
        /// `<c>asa/</c>` form first, then each spelling in <see cref="FallbackNames"/> under each
        /// prefix the bank might use. Pure function of the predicate, so it can be tested without
        /// a singer.
        /// </summary>
        public static string ResolveBankName(string symbol, Func<string, bool> singerHas) {
            if (string.IsNullOrEmpty(symbol)) {
                return null;
            }
            if (singerHas(symbol)) {
                return symbol;
            }
            var prefix = symbol.StartsWith(Prefix) ? Prefix : string.Empty;
            var bare = symbol.Substring(prefix.Length);
            // A token with an explicit entry has its acceptable spellings enumerated; a plain
            // name there is tried bare only, so no language can lend it a different phone.
            // A token WITHOUT an entry is unambiguous and keeps the generic prefix search.
            var curated = FallbackAliases.ContainsKey(bare);
            foreach (var name in FallbackNames(bare)) {
                if (name.IndexOf('/') >= 0) {
                    if (singerHas(name)) {
                        return name;
                    }
                    continue;
                }
                if (curated) {
                    if (singerHas(name)) {
                        return name;
                    }
                    continue;
                }
                foreach (var bankPrefix in BankPrefixes) {
                    var candidate = bankPrefix + name;
                    if (singerHas(candidate)) {
                        return candidate;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Name -> id pairs from a flat JSON object, tolerating duplicate keys and trailing commas.
        /// Some published banks repeat a key; `JsonDocument` rejects that outright, and losing the
        /// whole table over it would disable the placeholder-alias test for no reason.
        /// </summary>
        public static Dictionary<string, int> ParseNameToId(string json) {
            var ids = new Dictionary<string, int>();
            if (string.IsNullOrEmpty(json)) {
                return ids;
            }
            try {
                using (var doc = System.Text.Json.JsonDocument.Parse(json)) {
                    foreach (var property in doc.RootElement.EnumerateObject()) {
                        if (property.Value.TryGetInt32(out var id)) {
                            ids[property.Name] = id;
                        }
                    }
                    return ids;
                }
            } catch (Exception) {
                // fall through to the textual scan
            }
            foreach (System.Text.RegularExpressions.Match m
                     in System.Text.RegularExpressions.Regex.Matches(
                         json, @"""([^""]+)""\s*:\s*(\d+)")) {
                if (int.TryParse(m.Groups[2].Value, out var id)) {
                    ids[m.Groups[1].Value] = id;
                }
            }
            return ids;
        }

        /// <summary>
        /// A bank's symbol table, loaded exactly as OpenUtau loads it
        /// (<c>DiffSingerUtils.LoadPhonemes</c>): a <c>.json</c> file maps name to id, anything
        /// else is a flat list where the id is the line number. Several published banks ship the
        /// flat form, and reading it as JSON silently yields nothing.
        /// </summary>
        public static Dictionary<string, int> LoadNameToId(string path) {
            var ids = new Dictionary<string, int>();
            try {
                if (!File.Exists(path)) {
                    return ids;
                }
                var text = File.ReadAllText(path);
                if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)) {
                    return ParseNameToId(text);
                }
                var lines = text.Replace("\r", string.Empty).Split('\n');
                for (var i = 0; i < lines.Length; i++) {
                    var name = lines[i].TrimEnd('\r');
                    if (name.Length > 0) {
                        ids[name] = i;
                    }
                }
            } catch (Exception) {
                // an unreadable table is not fatal
            }
            return ids;
        }

        /// <summary>True when the token must be split even on a bank that names it.</summary>
        public static bool MustSplit(string token) =>
            !string.IsNullOrEmpty(token) && Splittable.TryGetValue(token, out var parts)
            && parts.AlwaysSplit;

        /// <summary>
        /// Splits a cluster the bank cannot really sing into base + glide.
        ///
        /// <para>Two reasons to split: the bank has no such phone at all, or the bank's cluster
        /// shares its base's token id — a placeholder alias of the plain phone, carrying none of
        /// the secondary articulation. Everything the bank really has is left alone, so a bank
        /// trained with its own `bw` or `kx` keeps them; the id test is what distinguishes that
        /// from the alias. (That is why `<c>asa/bw</c>` merely being present in a bank is not
        /// evidence that the bank can sing a labialised /b/ — when the placeholder pass wrote it,
        /// it points at `<c>asa/b</c>`.)</para>
        ///
        /// <para><c>MustSplit</c> survives only as a fallback for when the id table could not be
        /// read: with no ids there is no way to tell a real aspirated stop from an alias, and
        /// splitting is the safer guess on any bank of this family.</para>
        ///
        /// <para>Returns false when nothing should change, or when either half is missing, in
        /// which case the caller keeps the original and the host reports it as before.</para>
        /// </summary>
        /// <param name="isPlaceholderAlias">Given (cluster, base) token names, true when the bank
        /// resolves them to the same id. Null when the bank's id table could not be read.</param>
        public static bool TryDegrade(string symbol, Func<string, bool> singerHas,
                                      Func<string, string, bool> isPlaceholderAlias,
                                      out string[] replacement) {
            replacement = null;
            if (string.IsNullOrEmpty(symbol)) {
                return false;
            }
            var prefix = symbol.StartsWith(Prefix) ? Prefix : string.Empty;
            var bare = symbol.Substring(prefix.Length);
            if (!TrySplitCluster(bare, out var stem, out var glide)) {
                return false;
            }
            var head = prefix + stem;
            var tail = prefix + glide;
            var placeholderAlias = isPlaceholderAlias != null && isPlaceholderAlias(symbol, head);
            // aspiration splits unconditionally ONLY when the ids are unknown, as a fallback
            var blindAspirationSplit = MustSplit(bare) && isPlaceholderAlias == null;
            if (!blindAspirationSplit && singerHas(symbol) && !placeholderAlias) {
                return false;
            }
            if (!singerHas(head) || !singerHas(tail)) {
                return false;
            }
            replacement = new[] { head, tail };
            return true;
        }

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
