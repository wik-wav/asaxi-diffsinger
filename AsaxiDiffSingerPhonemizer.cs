using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// <c>dsdict-asa.yaml</c> for the SYMBOL TABLE (names, types, which phones the bank can
    /// actually sing) and merges it with a rule G2P that supplies the WORDS. That split is the
    /// point: the dictionary never needs one entry per word, only one entry per phone.
    ///
    /// <para><b>Prefixed output.</b> <c>GetLangCode()</c> returning <c>"asa"</c> is what makes
    /// the host rewrite every bare phone this G2P emits into <c>asa/&lt;phone&gt;</c>, exactly as
    /// <c>[DIFFS EN]</c> emits <c>en/*</c> for a multi-language bank.</para>
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
        /// Wraps the loaded G2P with the two things the host cannot express itself: a phonetic hint
        /// may name any phone the singer can sing, and a cluster the bank does not really have is
        /// split into its base phone plus a glide.
        /// </summary>
        protected override IG2p LoadG2p(string rootPath, bool useLangId = false) {
            return new SingerVocabularyG2p(base.LoadG2p(rootPath, useLangId),
                                           IsPhonemeSupported,
                                           TryReadTokenIds(rootPath));
        }

        /// <summary>
        /// The bank's own name -> id table, read so a placeholder alias can be told apart from a
        /// real phone: `<c>asa/bw</c>` pointing at the id of `<c>asa/b</c>` is not a labialised
        /// consonant, it is the plain one under another name, and must be split.
        /// </summary>
        static Dictionary<string, int> TryReadTokenIds(string rootPath) {
            var ids = new Dictionary<string, int>();
            try {
                var path = Path.Combine(rootPath, "phonemes.json");
                if (!File.Exists(path)) {
                    return ids;
                }
                using (var doc = JsonDocument.Parse(File.ReadAllText(path))) {
                    foreach (var property in doc.RootElement.EnumerateObject()) {
                        if (property.Value.TryGetInt32(out var id)) {
                            ids[property.Name] = id;
                        }
                    }
                }
            } catch (Exception) {
                // An unreadable table is not fatal: without it the split falls back to
                // "is the phone absent", which is the pre-existing behaviour.
            }
            return ids;
        }

        /// <summary>Delegates everything; widens <c>IsValidSymbol</c> and splits absent clusters.</summary>
        sealed class SingerVocabularyG2p : IG2p {
            readonly IG2p inner;
            readonly Func<string, bool> singerHas;
            readonly Dictionary<string, int> tokenIds;

            public SingerVocabularyG2p(IG2p inner, Func<string, bool> singerHas,
                                       Dictionary<string, int> tokenIds) {
                this.inner = inner;
                this.singerHas = singerHas;
                this.tokenIds = tokenIds;
            }

            public bool IsValidSymbol(string symbol) =>
                inner.IsValidSymbol(symbol)
                || (!string.IsNullOrEmpty(symbol) && singerHas(symbol));

            public bool IsVowel(string symbol) => inner.IsVowel(symbol);

            public bool IsGlide(string symbol) => inner.IsGlide(symbol);

            /// <summary>
            /// Splits a cluster the bank does not really have into base + glide: <c>kw</c> to
            /// <c>k</c> + <c>w</c>, <c>ky</c> to <c>k</c> + <c>j</c>, and an aspirated stop to
            /// stop + <c>hh</c>.
            /// </summary>
            public string[] Query(string grapheme) {
                var phonemes = inner.Query(grapheme);
                if (phonemes == null) {
                    return null;
                }
                var outPhonemes = new List<string>(phonemes.Length);
                foreach (var symbol in phonemes) {
                    if (AsaxiG2p.TryDegrade(symbol, singerHas, SharesIdWithBase, out var degrades)) {
                        outPhonemes.AddRange(degrades);
                    } else {
                        outPhonemes.Add(symbol);
                    }
                }
                return outPhonemes.ToArray();
            }

            /// <summary>
            /// True when the bank's cluster token and its base token are the SAME token id — the
            /// signature of a placeholder alias, which carries none of the secondary articulation.
            /// </summary>
            bool SharesIdWithBase(string cluster, string stem) =>
                tokenIds.TryGetValue(cluster, out var clusterId)
                && tokenIds.TryGetValue(stem, out var stemId)
                && clusterId == stemId;

            public string[] UnpackHint(string hint, char separator = ' ') =>
                inner.UnpackHint(hint, separator);
        }
    }
}
