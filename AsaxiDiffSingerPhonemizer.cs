using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenUtau.Api;
using OpenUtau.Core.DiffSinger;

namespace Asaxi {
    /// <summary>
    /// DiffSinger phonemizer for Asaxi.
    ///
    /// <para>The attribute's <c>language</c> is <c>"ASA: Asaxi"</c>, not the bare code <c>"ASA"</c>.
    /// OpenUtau groups the phonemizer menu by that string verbatim, and its heading is the key
    /// itself when the theme has no <c>languages.&lt;key&gt;</c> translation — which it does not for
    /// Asaxi. The existing Asaxi CVVC plugin declares this same display string, so matching it is
    /// what makes both Asaxi phonemizers share one "ASA: Asaxi" submenu instead of getting a bare
    /// "ASA" heading each. It does not affect phoneme output: the <c>asa/</c> prefix comes from
    /// <see cref="GetLangCode"/>, which stays <c>asa</c>.</para>
    ///
    /// It is a <see cref="DiffSingerG2pPhonemizer"/>, so the host still reads the singer's
    /// <c>dsdict-asa.yaml</c> for the SYMBOL TABLE and merges it with a rule G2P that supplies the
    /// WORDS. That split is the point: the dictionary never needs one entry per word, only one
    /// entry per phone.
    /// </summary>
    [Phonemizer("DiffSinger Asaxi Phonemizer", "DIFFS ASA", "wik_wav", "ASA: Asaxi")]
    public class DiffSingerAsaxiPhonemizer : DiffSingerG2pPhonemizer {
        /// <summary>
        /// The singer-side dictionary this phonemizer asks for. It carries the <c>symbols:</c>
        /// block (type of every <c>asa/*</c> phone) and may carry no <c>entries:</c> at all,
        /// because words come from the rules.
        /// </summary>
        protected override string GetDictionaryName() => "dsdict-asa.yaml";

        /// <summary>Language code; the bank's language id and the <c>asa/</c> prefix both use it.</summary>
        public override string GetLangCode() => "asa";

        protected override IG2p LoadBaseG2p() => new AsaxiG2p();

        protected override string[] GetBaseG2pVowels() => AsaxiG2p.Vowels.Concat(AsaxiG2p.Nuclei).ToArray();

        protected override string[] GetBaseG2pConsonants() => AsaxiG2p.Consonants;

        /// <summary>
        /// Wraps the loaded G2P with the things the host cannot express itself: a hint may name any
        /// phone the singer can sing, an Asaxi token is looked up under the bank's own spelling,
        /// and a cluster the bank does not really have is split into base + glide.
        /// </summary>
        protected override IG2p LoadG2p(string rootPath, bool useLangId = false) {
            return new SingerVocabularyG2p(base.LoadG2p(rootPath, useLangId),
                                           IsPhonemeSupported,
                                           TryReadTokenIds(rootPath));
        }

        /// <summary>The symbol table's path as the bank's own dsconfig declares it.</summary>
        static string DeclaredPhonemesFile(string rootPath) {
            try {
                var dsconfig = Path.Combine(rootPath, "dsconfig.yaml");
                if (File.Exists(dsconfig)) {
                    foreach (var line in File.ReadAllLines(dsconfig)) {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("phonemes:", StringComparison.Ordinal)) {
                            var value = trimmed.Substring("phonemes:".Length).Trim().Trim('"', '\'');
                            if (value.Length > 0) {
                                return value.Replace('/', Path.DirectorySeparatorChar);
                            }
                        }
                    }
                }
            } catch (Exception) {
                // fall through to the default name
            }
            return "phonemes.json";
        }

        /// <summary>
        /// The bank's own name -> id table. Read so a placeholder alias can be told apart from a
        /// real phone: `<c>asa/bw</c>` pointing at the id of `<c>asa/b</c>` is not a labialised
        /// consonant, it is the plain one under another name, and must be split. The file is
        /// wherever the bank's dsconfig says it is — not always the singer root.
        /// </summary>
        static Dictionary<string, int> TryReadTokenIds(string rootPath) {
            try {
                var path = Path.Combine(rootPath, DeclaredPhonemesFile(rootPath));
                return AsaxiG2p.LoadNameToId(path);
            } catch (Exception) {
                // An unreadable table is not fatal: the split then falls back to "is it absent".
                return new Dictionary<string, int>();
            }
        }

        /// <summary>Delegates everything; widens symbol validity and rewrites unsingable phones.</summary>
        sealed class SingerVocabularyG2p : IG2p {
            readonly IG2p inner;
            readonly Func<string, bool> singerHas;
            readonly Dictionary<string, int> tokenIds;
            readonly bool idsKnown;

            public SingerVocabularyG2p(IG2p inner, Func<string, bool> singerHas,
                                       Dictionary<string, int> tokenIds) {
                this.inner = inner;
                this.singerHas = singerHas;
                this.tokenIds = tokenIds;
                this.idsKnown = tokenIds.Count > 0;
            }

            public bool IsValidSymbol(string symbol) =>
                inner.IsValidSymbol(symbol)
                || (!string.IsNullOrEmpty(symbol) && singerHas(symbol))
                || Resolve(symbol) != null;

            public bool IsVowel(string symbol) => inner.IsVowel(symbol);

            public bool IsGlide(string symbol) => inner.IsGlide(symbol);

            /// <summary>
            /// Rewrites each phone the rules produced into something the singer can actually sing:
            /// the bank's own spelling of it, or — for a cluster the bank does not have — its base
            /// plus its glide. Anything unresolvable is passed through and the host reports it.
            /// </summary>
            public string[] Query(string grapheme) {
                var phonemes = inner.Query(grapheme);
                if (phonemes == null) {
                    return null;
                }
                var outPhonemes = new List<string>(phonemes.Length);
                foreach (var symbol in phonemes) {
                    Emit(symbol, outPhonemes);
                }
                return outPhonemes.ToArray();
            }

            void Emit(string symbol, List<string> output) {
                var prefix = symbol.StartsWith(AsaxiG2p.Prefix) ? AsaxiG2p.Prefix : string.Empty;
                var bare = symbol.Substring(prefix.Length);
                var present = singerHas(symbol);
                var alias = present
                            && AsaxiG2p.TrySplitCluster(bare, out var stem, out _)
                            && SharesIdWithBase(symbol, prefix + stem);
                // aspiration splits as a fallback when the id table is unavailable
                var blindAspiration = !idsKnown && AsaxiG2p.MustSplit(bare);

                if ((!present || alias || blindAspiration)
                    && AsaxiG2p.TrySplitCluster(bare, out var head, out var glide)) {
                    var headName = Resolve(prefix + head);
                    var glideName = Resolve(prefix + glide);
                    if (headName != null && glideName != null) {
                        output.Add(headName);
                        output.Add(glideName);
                        return;
                    }
                }
                output.Add(Resolve(symbol) ?? symbol);
            }

            /// <summary>
            /// The bank's name for an Asaxi token: `asa/&lt;token&gt;` if the bank has it, else the
            /// token's alternative spellings under any language prefix the bank uses, else null.
            /// </summary>
            string Resolve(string symbol) => AsaxiG2p.ResolveBankName(symbol, singerHas);

            /// <summary>
            /// True when the bank's cluster token and its base token are the SAME token id — the
            /// signature of a placeholder alias, which carries none of the secondary articulation.
            /// </summary>
            bool SharesIdWithBase(string cluster, string stem) =>
                idsKnown
                && tokenIds.TryGetValue(cluster, out var clusterId)
                && tokenIds.TryGetValue(stem, out var stemId)
                && clusterId == stemId;

            public string[] UnpackHint(string hint, char separator = ' ') =>
                inner.UnpackHint(hint, separator);
        }
    }
}
