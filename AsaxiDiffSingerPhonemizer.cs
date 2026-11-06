using System.Linq;
using OpenUtau.Api;
using OpenUtau.Core.DiffSinger;

namespace Asaxi {
    /// <summary>
    /// DiffSinger phonemizer for Asaxi.
    ///
    /// It is a <see cref="DiffSingerG2pPhonemizer"/>, so the host still reads the singer's
    /// <c>dsdict-asa.yaml</c> for the SYMBOL TABLE (names, types, which phones the bank can
    /// actually sing) and merges it with a rule G2P that supplies the WORDS. That split is the
    /// point: the dictionary never needs one entry per word, only one entry per phone.
    ///
    /// <para><b>Prefixed output.</b> <c>GetLangCode()</c> returning <c>"asa"</c> is what makes
    /// the host rewrite every bare phone this G2P emits into <c>asa/&lt;phone&gt;</c>, exactly as
    /// <c>[DIFFS EN]</c> emits <c>en/*</c> for a multi-language bank. A bank that lacks
    /// <c>asa/kx</c> falls back to the bare name for that one phone rather than failing the
    /// whole word.</para>
    /// </summary>
    [Phonemizer("DiffSinger Asaxi Phonemizer", "DIFFS ASA", "wik_wav", "ASA")]
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
    }
}
