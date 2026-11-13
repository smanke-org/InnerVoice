// The voice packs: the model files Qwen3-TTS and Orpheus need, pinned to one revision of each
// Hugging Face repository and checked by SHA-256, downloaded into
// %LOCALAPPDATA%\InnerVoice\Voice Models (APP_VOICE_MODELS in tests, as on the Mac). Like the
// Mac's VoiceModels.swift, one pack per engine; unlike it, the files are ONNX exports, and
// the two Qwen3-TTS models share their tokenizer and audio decoder.

using System.Security.Cryptography;

namespace InnerVoice.Core.Speech.Neural;

/// A Hugging Face repository at one commit.
public sealed record VoiceRepo(string Id, string Revision, string License, string Credit);

/// One file of a pack: where it comes from and where it goes under the Voice Models folder.
public sealed record VoicePackFile(VoiceRepo Repo, string Path, string Local, long Bytes, string Sha256)
{
    public string Url => $"https://huggingface.co/{Repo.Id}/resolve/{Repo.Revision}/{Path}";
}

public sealed record VoicePack(VoiceEngine Engine, string Title, string Summary, IReadOnlyList<VoicePackFile> Files,
                               int RecommendedMemoryGB, int MinimumMemoryGB)
{
    public long Bytes => Files.Sum(f => f.Bytes);
    public string SizeDescription => $"{Bytes / 1e9:0.0} GB";
    public IEnumerable<VoiceRepo> Repos => Files.Select(f => f.Repo).Distinct();
}

public static class VoicePacks
{
    static readonly VoiceRepo Design = new("wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX", "62c7863a68800d72bcee4f2931148c441571ace7",
        "Apache-2.0", "Qwen3-TTS by the Qwen team (Alibaba Cloud), ONNX export by WaveKat");
    static readonly VoiceRepo Clone = new("wavekat/Qwen3-TTS-0.6B-Base-ONNX", "177cd39e2ddab0d4d7ce5152a7abc2d382621158",
        "Apache-2.0", "Qwen3-TTS by the Qwen team (Alibaba Cloud), ONNX export by WaveKat");
    static readonly VoiceRepo OrpheusLm = new("onnx-community/orpheus-3b-0.1-ft-ONNX", "03791aba66ad8415c41090919053b6fcc7a7a256",
        "Apache-2.0 (Orpheus), Llama 3.2 Community License (its base model)", "Orpheus by Canopy Labs, built on Meta’s Llama 3.2; ONNX export by the ONNX Community");
    static readonly VoiceRepo Snac = new("onnx-community/snac_24khz-ONNX", "f317cdff7efb7b2a63148c9e68bbb0320fe629a1",
        "MIT", "SNAC by Hubert Siuzdak; ONNX export by the ONNX Community");

    static readonly VoicePackFile[] All =
    [
    // wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX @ 62c7863a68800d72bcee4f2931148c441571ace7
    new(Design, "config.json", "qwen/design/config.json", 1477, "a31bc3d9967a3f00057017f962ed77d7f20ed1a10635ec61a088e689448c14d7"),
    new(Design, "embeddings/cp_codec_embedding_0.npy", "qwen/design/embeddings/cp_codec_embedding_0.npy", 16777344, "d817a2fddbd2b6b4a48fc82eb6dae03c7e85976c3c978632ef8d3d463f405e8f"),
    new(Design, "embeddings/cp_codec_embedding_1.npy", "qwen/design/embeddings/cp_codec_embedding_1.npy", 16777344, "dacdb94f57da9b2c52ac7122c44bbe5d0b0161c01bb959fcec20aadfefadd557"),
    new(Design, "embeddings/cp_codec_embedding_10.npy", "qwen/design/embeddings/cp_codec_embedding_10.npy", 16777344, "223221fe7d3812e87ab768772ad43ece3bcdc4fd3a9a1553fdac728d5804dc06"),
    new(Design, "embeddings/cp_codec_embedding_11.npy", "qwen/design/embeddings/cp_codec_embedding_11.npy", 16777344, "70032ec9f813bab56ae782c225afd6923d4fab61693ea7d9031b25e5801633d7"),
    new(Design, "embeddings/cp_codec_embedding_12.npy", "qwen/design/embeddings/cp_codec_embedding_12.npy", 16777344, "8273bcfe29249ce1f40b8a2f50a221757cfd20a1470fff0dad0af1ee3fbd981b"),
    new(Design, "embeddings/cp_codec_embedding_13.npy", "qwen/design/embeddings/cp_codec_embedding_13.npy", 16777344, "e98208b4cfe5b7ad0d587ff6102bffdef64cddbd501ec0a74a2b216717170259"),
    new(Design, "embeddings/cp_codec_embedding_14.npy", "qwen/design/embeddings/cp_codec_embedding_14.npy", 16777344, "c08e5712f1aa9da944c8725e53e5c7294eee4260e4d686e8b2add990bc85550d"),
    new(Design, "embeddings/cp_codec_embedding_2.npy", "qwen/design/embeddings/cp_codec_embedding_2.npy", 16777344, "cd159613ad05d6c13b110cfd897e58c2dcc4992b16b65a8885195e2a85bc4f22"),
    new(Design, "embeddings/cp_codec_embedding_3.npy", "qwen/design/embeddings/cp_codec_embedding_3.npy", 16777344, "591f29341005ef44a08374f7db6fa09331bfd40099222a30841a6a30d468307f"),
    new(Design, "embeddings/cp_codec_embedding_4.npy", "qwen/design/embeddings/cp_codec_embedding_4.npy", 16777344, "b3475def6be6832ec81be51ca1a578cc602dfd5e8da943514d54590263c494ea"),
    new(Design, "embeddings/cp_codec_embedding_5.npy", "qwen/design/embeddings/cp_codec_embedding_5.npy", 16777344, "1c6b04c82af4377f570205b7fe012b19c1fbe145e55bd23ded18b17817afd02e"),
    new(Design, "embeddings/cp_codec_embedding_6.npy", "qwen/design/embeddings/cp_codec_embedding_6.npy", 16777344, "818807b351bf14a35673f0947fdb56b5cd5603b89ee794479d75c74d19c1058b"),
    new(Design, "embeddings/cp_codec_embedding_7.npy", "qwen/design/embeddings/cp_codec_embedding_7.npy", 16777344, "ab66016da143999347eb38855dbf4960043895b642ed217260b020020c43bc75"),
    new(Design, "embeddings/cp_codec_embedding_8.npy", "qwen/design/embeddings/cp_codec_embedding_8.npy", 16777344, "78c897e85d9f7ed30d15f9d7d3b2c74b8ce36ff61314a7b81712b39cb4a7e22b"),
    new(Design, "embeddings/cp_codec_embedding_9.npy", "qwen/design/embeddings/cp_codec_embedding_9.npy", 16777344, "034ca0184c8e546d6de7e61dcdcab8632260e034e91727ba0c7b6e9d7fef997c"),
    new(Design, "embeddings/small_to_mtp_projection_bias.npy", "qwen/design/embeddings/small_to_mtp_projection_bias.npy", 4224, "496229ae9d0269ac93f84f688a1481e0e8735cef50417ac2c7d12955e0352e4a"),
    new(Design, "embeddings/small_to_mtp_projection_weight.npy", "qwen/design/embeddings/small_to_mtp_projection_weight.npy", 8388736, "e57c0515ff31947cd90ff730c9253ec533825788b4572fc7e49980107b42f188"),
    new(Design, "embeddings/talker_codec_embedding.npy", "qwen/design/embeddings/talker_codec_embedding.npy", 25165952, "1140e81db3203d0e9118569f264271174588115d29b257a6a857be5fd4692a2f"),
    new(Design, "embeddings/text_embedding.npy", "qwen/design/embeddings/text_embedding.npy", 1244659840, "843354d7f837c2f8ac1e0708320226a98bf4b03057e34da59f8ff6dd197b2054"),
    new(Design, "embeddings/text_projection_fc1_bias.npy", "qwen/design/embeddings/text_projection_fc1_bias.npy", 8320, "3eb97f665af043f1f1242fbcc9fab2428558f70df7814ab3a21cfe559eabb52c"),
    new(Design, "embeddings/text_projection_fc1_weight.npy", "qwen/design/embeddings/text_projection_fc1_weight.npy", 16777344, "3314a298c2fb0042924b2cd75aa8cdc065cec6bd545ae557ad5c5ddf4a1a988c"),
    new(Design, "embeddings/text_projection_fc2_bias.npy", "qwen/design/embeddings/text_projection_fc2_bias.npy", 8320, "fc5a6eae755bd96a9d8d0d36bb8dc63473e2146fb1d703f5e461a5fbcb70e522"),
    new(Design, "embeddings/text_projection_fc2_weight.npy", "qwen/design/embeddings/text_projection_fc2_weight.npy", 16777344, "9f3205a265ffbf2c7e2a2f68f6c9a85913e14499a4225cf005ffd9f2613b597b"),
    new(Design, "int4/code_predictor.onnx", "qwen/design/int4/code_predictor.onnx", 361280, "7fbf1d23c9d49a1f375cc2c936e2d9602c9ec2bf283ce940008349d5c68a661a"),
    new(Design, "int4/code_predictor.onnx.data", "qwen/design/int4/code_predictor.onnx.data", 337543168, "a08526a6b2e5f013117658930dfbe4ae3c24156f3f0ae6db92d0a2034baf0a32"),
    new(Design, "int4/talker_decode.onnx", "qwen/design/int4/talker_decode.onnx", 4075438, "b19a61dcff0425e9f75fadf14f7cfb40ef3602ca652652d09a695a559ebbaaef"),
    new(Design, "int4/talker_decode.onnx.data", "qwen/design/int4/talker_decode.onnx.data", 1504985088, "df06bacf92ef2f5cd8b63840d39067593fecf6a22613ebff5a1ee0df2725080e"),
    new(Design, "int4/talker_prefill.onnx", "qwen/design/int4/talker_prefill.onnx", 4310083, "8deacf194b588e0ce36fa3ba46f7754807190d33872d451c2b8f5301356a5ab0"),
    new(Design, "int4/talker_prefill.onnx.data", "qwen/design/int4/talker_prefill.onnx.data", 1504985088, "df06bacf92ef2f5cd8b63840d39067593fecf6a22613ebff5a1ee0df2725080e"),
    new(Design, "int4/vocoder.onnx", "qwen/shared/int4/vocoder.onnx", 3154931, "d1cf6f6b77d728c4484f9277ec886dcbf06b4f4b0310c99be34d7bc6a516959e"),
    new(Design, "int4/vocoder.onnx.data", "qwen/shared/int4/vocoder.onnx.data", 912219264, "d29e56c20bc6ff8d996daaae914d5d1ec1cd8170be578dc792b44978208a9575"),
    new(Design, "tokenizer/merges.txt", "qwen/shared/tokenizer/merges.txt", 1671853, "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5"),
    new(Design, "tokenizer/vocab.json", "qwen/shared/tokenizer/vocab.json", 2776833, "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910"),
    // wavekat/Qwen3-TTS-0.6B-Base-ONNX @ 177cd39e2ddab0d4d7ce5152a7abc2d382621158
    new(Clone, "config.json", "qwen/clone/config.json", 1462, "9f6444a4e6c6a2bb238116957f2852713c447a581f791dcea088e6e6bf9bb55c"),
    new(Clone, "embeddings/cp_codec_embedding_0.npy", "qwen/clone/embeddings/cp_codec_embedding_0.npy", 8388736, "2cf4b5d6f6fca977657bc82634eea275cd072036a6a16484febd41b5445abff1"),
    new(Clone, "embeddings/cp_codec_embedding_1.npy", "qwen/clone/embeddings/cp_codec_embedding_1.npy", 8388736, "c8655ecd642f17d04a85ea37c2665205a8666843925227b25dc8c8bceb8015f5"),
    new(Clone, "embeddings/cp_codec_embedding_10.npy", "qwen/clone/embeddings/cp_codec_embedding_10.npy", 8388736, "bf17275629b464a4a38826beeb79b99e0e58cc02c530da2e572b9b7d9972fb2d"),
    new(Clone, "embeddings/cp_codec_embedding_11.npy", "qwen/clone/embeddings/cp_codec_embedding_11.npy", 8388736, "dee4af40f34626a4951fba2b894120c8cf8293024ae068d1f2579f356c6e2284"),
    new(Clone, "embeddings/cp_codec_embedding_12.npy", "qwen/clone/embeddings/cp_codec_embedding_12.npy", 8388736, "e307b70fbaddc6e7e48259b4e8c9417f24b155be9281205f38bbf40c64359951"),
    new(Clone, "embeddings/cp_codec_embedding_13.npy", "qwen/clone/embeddings/cp_codec_embedding_13.npy", 8388736, "cc15bfc4290be77523f8f14c78b9b1710042b5d078291ee87038dfb7ecb3f185"),
    new(Clone, "embeddings/cp_codec_embedding_14.npy", "qwen/clone/embeddings/cp_codec_embedding_14.npy", 8388736, "aa0de98365775e1cb968514595ff7e23936bea21c2bda6e5bab7e7052cdf97ba"),
    new(Clone, "embeddings/cp_codec_embedding_2.npy", "qwen/clone/embeddings/cp_codec_embedding_2.npy", 8388736, "5436624853b3091bc1b88cf37a4d60af46346c508237d72e597bed561a5b2138"),
    new(Clone, "embeddings/cp_codec_embedding_3.npy", "qwen/clone/embeddings/cp_codec_embedding_3.npy", 8388736, "1041b78ea5158c96cda4aae45bd21707a6fe8c92e6831715075d70d7b5b8e77a"),
    new(Clone, "embeddings/cp_codec_embedding_4.npy", "qwen/clone/embeddings/cp_codec_embedding_4.npy", 8388736, "17af8770454ab26ca82f092d0d5a634680e4d14412d327fb8b6f94a57b256800"),
    new(Clone, "embeddings/cp_codec_embedding_5.npy", "qwen/clone/embeddings/cp_codec_embedding_5.npy", 8388736, "b9abbf163b50ba0207e2196772c67b2e1025cf7f4fbe378c6a44123869253ad9"),
    new(Clone, "embeddings/cp_codec_embedding_6.npy", "qwen/clone/embeddings/cp_codec_embedding_6.npy", 8388736, "1fb06414cb7d4edcd73142e79cbb4bd46f918517e5812b73345ade9535c6eafb"),
    new(Clone, "embeddings/cp_codec_embedding_7.npy", "qwen/clone/embeddings/cp_codec_embedding_7.npy", 8388736, "08b8d7bd125d75ffa42f515153e66ade42cb16c2eea6ebe1935a2ac2509cbad0"),
    new(Clone, "embeddings/cp_codec_embedding_8.npy", "qwen/clone/embeddings/cp_codec_embedding_8.npy", 8388736, "a2cf331bb3e2f2c0d44c91c55af045b9c975199331e4566a373b883ce04e9964"),
    new(Clone, "embeddings/cp_codec_embedding_9.npy", "qwen/clone/embeddings/cp_codec_embedding_9.npy", 8388736, "389422edef8c0b6dbf6cdb48359a0274b11984c04668c004810866784dab2942"),
    new(Clone, "embeddings/talker_codec_embedding.npy", "qwen/clone/embeddings/talker_codec_embedding.npy", 12583040, "47fa9e30f98b1528fc9b332d314f22a32fa33e187509a4d3537f8b2c31199e39"),
    new(Clone, "embeddings/text_embedding.npy", "qwen/clone/embeddings/text_embedding.npy", 1244659840, "e49a5ebb22075609520b91db99c33cf7590cefddc96d574a725a353ff0b1f480"),
    new(Clone, "embeddings/text_projection_fc1_bias.npy", "qwen/clone/embeddings/text_projection_fc1_bias.npy", 8320, "2ca292516e9574fa67e0174a49770051156246bb0679096f0514286091eea7c3"),
    new(Clone, "embeddings/text_projection_fc1_weight.npy", "qwen/clone/embeddings/text_projection_fc1_weight.npy", 16777344, "94b62d00b306c5db843ac86dc135354889a2ce40d7adce79ae23f63c3d6acf19"),
    new(Clone, "embeddings/text_projection_fc2_bias.npy", "qwen/clone/embeddings/text_projection_fc2_bias.npy", 4224, "0725b7f7d0318f430723177d046eaf3b08c7a65d4a4a6dd264d61e574e66ff19"),
    new(Clone, "embeddings/text_projection_fc2_weight.npy", "qwen/clone/embeddings/text_projection_fc2_weight.npy", 8388736, "d6d31d3bca45fee1d05612f3c9eb26e0c34587be52d2c34c208c79e6656f15a6"),
    new(Clone, "int4/code_predictor.onnx", "qwen/clone/int4/code_predictor.onnx", 360475, "6077ceb8fd1dd2c9e4533d564d917b913f4a4bf1056ee946f99dcb56dff3234f"),
    new(Clone, "int4/code_predictor.onnx.data", "qwen/clone/int4/code_predictor.onnx.data", 335306752, "78b530db39646d04ce8dbe60ae35e814ba05d832f345d828ed6ad69483af8b43"),
    new(Clone, "int4/talker_decode.onnx", "qwen/clone/int4/talker_decode.onnx", 3779217, "d37e92e3d9ac0ec80044f0f65f1d4cb86f5f9e804e82883b1f38bf59e4786271"),
    new(Clone, "int4/talker_decode.onnx.data", "qwen/clone/int4/talker_decode.onnx.data", 471736320, "7365dec88da097e8ff0f19e33cc6effe075ff387a8c6c0bb8b0725542be587b0"),
    new(Clone, "int4/talker_prefill.onnx", "qwen/clone/int4/talker_prefill.onnx", 3996526, "4942858b31e4c000ada5f236e83208a3eb7da2016497304f8224c0a60374a5e8"),
    new(Clone, "int4/talker_prefill.onnx.data", "qwen/clone/int4/talker_prefill.onnx.data", 471736320, "7365dec88da097e8ff0f19e33cc6effe075ff387a8c6c0bb8b0725542be587b0"),
    new(Clone, "speaker_encoder.onnx", "qwen/clone/speaker_encoder.onnx", 218282, "fa067d83976b96c98ba302ada0f03dde7c85189953fd1c7a178fc32e99cb8da8"),
    new(Clone, "speaker_encoder.onnx.data", "qwen/clone/speaker_encoder.onnx.data", 35409920, "9f6f588deda5ebbcda0a6b010bded2669faf861f402cc5ba5556c58d5c0aeb54"),
    new(Clone, "tokenizer_encoder.onnx", "qwen/clone/tokenizer_encoder.onnx", 553938, "98284239976a637a229df22207b6ef34ff9a487890cfd00410e9c0facf7c182a"),
    new(Clone, "tokenizer_encoder.onnx.data", "qwen/clone/tokenizer_encoder.onnx.data", 192305920, "4d8a454094b2f58265f4dab7545211f73331f78888a36a9039272818f51ecf2c"),
    // onnx-community/orpheus-3b-0.1-ft-ONNX @ 03791aba66ad8415c41090919053b6fcc7a7a256
    new(OrpheusLm, "config.json", "orpheus/config.json", 1158, "e43c6ad5c3d0aa5782ac69849d1305fc0adfbdde496dbfcffd138d9514ee93d1"),
    new(OrpheusLm, "onnx/model_q4.onnx", "orpheus/onnx/model_q4.onnx", 281966, "a7bdfbdb63768b08ccb70b59dcad21595b34dc2ba0f212184d2408a283adc61c"),
    new(OrpheusLm, "onnx/model_q4.onnx_data", "orpheus/onnx/model_q4.onnx_data", 2085583936, "87536d71853fb2d69b9a91681783a552b0ec2229a01403acf17f100a8cda3982"),
    new(OrpheusLm, "onnx/model_q4.onnx_data_1", "orpheus/onnx/model_q4.onnx_data_1", 337790976, "0e3f53730fdb75b148e29e405a5106c407c8ffc87e2f86674cdfa8d4f518c84f"),
    new(OrpheusLm, "tokenizer.json", "orpheus/tokenizer.json", 15722697, "f17508bf9f772747e43b038fae379a4c3e80f5a7d39da8123bbf9794b94f20b6"),
    // onnx-community/snac_24khz-ONNX @ f317cdff7efb7b2a63148c9e68bbb0320fe629a1
    new(Snac, "onnx/decoder_model.onnx", "orpheus/snac/decoder_model.onnx", 52600822, "9e2991fce96c3b49c508304e656052f4f6b860fe8432164fcd976eee195fd4f0"),
    ];

    /// Qwen3-TTS: designs a voice from a description and speaks every line in it. The Mac's text.
    public static readonly VoicePack Qwen = new(VoiceEngine.Qwen, "Qwen3-TTS",
        "A voice designed for each character from their summary and Voice Notes, kept the same on every line and acted in each tone.",
        All.Where(f => f.Local.StartsWith("qwen/", StringComparison.Ordinal)).ToList(), RecommendedMemoryGB: 16, MinimumMemoryGB: 8);

    /// Orpheus: eight expressive voices that laugh, sigh and gasp. The Mac's text.
    public static readonly VoicePack Orpheus = new(VoiceEngine.Orpheus, "Orpheus",
        "Eight natural American voices that act out the words, with sighs, laughs and gasps from the speech tags.",
        All.Where(f => f.Local.StartsWith("orpheus/", StringComparison.Ordinal)).ToList(), RecommendedMemoryGB: 16, MinimumMemoryGB: 8);

    public static IReadOnlyList<VoicePack> Packs { get; } = [Qwen, Orpheus];

    public static VoicePack? For(VoiceEngine engine) => engine switch
    {
        VoiceEngine.Qwen => Qwen,
        VoiceEngine.Orpheus => Orpheus,
        _ => null,
    };

    /// The Voice Models folder. The app sets it at startup (test runs: a temporary folder);
    /// APP_VOICE_MODELS overrides it, as on the Mac.
    public static string Folder
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("APP_VOICE_MODELS") is { Length: > 0 } env ? env : folder
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnerVoice", "Voice Models");
            return dir;
        }
        set { folder = value; Forget(); }
    }
    static string? folder;

    public static string PathOf(VoicePackFile f) => Path.Combine(Folder, f.Local.Replace('/', Path.DirectorySeparatorChar));
    public static string QwenDesignFolder => Path.Combine(Folder, "qwen", "design");
    public static string QwenCloneFolder => Path.Combine(Folder, "qwen", "clone");
    public static string QwenSharedFolder => Path.Combine(Folder, "qwen", "shared");
    public static string OrpheusFolder => Path.Combine(Folder, "orpheus");
    public static string SamplesFolder => Path.Combine(Folder, "Samples");

    static readonly Dictionary<VoiceEngine, bool> installed = [];
    static readonly Lock gate = new();

    /// Every file is in place at its full size (each was checked by SHA-256 before it was
    /// moved there). Cached until a download or removal: views ask on every redraw.
    public static bool IsInstalled(VoicePack pack)
    {
        lock (gate)
        {
            if (installed.TryGetValue(pack.Engine, out var known)) return known;
            bool all = pack.Files.All(f => new FileInfo(PathOf(f)) is { Exists: true } fi && fi.Length == f.Bytes);
            installed[pack.Engine] = all;
            return all;
        }
    }

    public static bool IsInstalled(VoiceEngine engine) => For(engine) is { } p && IsInstalled(p);

    /// Bytes already downloaded (finished files and .part files).
    public static long BytesPresent(VoicePack pack) => pack.Files.Sum(f =>
        File.Exists(PathOf(f)) ? f.Bytes : new FileInfo(PathOf(f) + ".part") is { Exists: true } part ? Math.Min(part.Length, f.Bytes) : 0);

    public static void Forget()
    {
        lock (gate) installed.Clear();
    }

    /// Removes a pack's files (Qwen3-TTS: and its voice samples). Files another pack still
    /// needs stay.
    public static void Remove(VoicePack pack)
    {
        var keep = Packs.Where(p => p != pack).SelectMany(p => p.Files).Select(f => f.Local).ToHashSet();
        foreach (var f in pack.Files.Where(f => !keep.Contains(f.Local)))
        {
            try { File.Delete(PathOf(f)); } catch (IOException) { }
            try { File.Delete(PathOf(f) + ".part"); } catch (IOException) { }
        }
        if (pack.Engine == VoiceEngine.Qwen && Directory.Exists(SamplesFolder))
            try { Directory.Delete(SamplesFolder, recursive: true); } catch (IOException) { }
        Forget();
    }
}

/// Progress of a pack download: a share of the bytes, or checking them.
public readonly record struct VoicePackProgress(double Fraction, long Bytes, bool Verifying);

/// Downloads a pack: each file to a .part file (resuming one a cancel or a dropped connection
/// left), checked by SHA-256, then moved into place, so a file in place is always whole.
public static class VoicePackDownloader
{
    public sealed class NotEnoughSpaceException(string message) : IOException(message);

    /// Room for the rest of the pack and a gigabyte to spare.
    public static void CheckSpace(VoicePack pack)
    {
        long needed = pack.Bytes - VoicePacks.BytesPresent(pack) + 1_000_000_000;
        try
        {
            Directory.CreateDirectory(VoicePacks.Folder);
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(VoicePacks.Folder))!).AvailableFreeSpace;
            if (free < needed) throw new NotEnoughSpaceException($"Not enough free space: {needed / 1e9:0.0} GB needed.");
        }
        catch (NotEnoughSpaceException) { throw; }
        catch (Exception) { /* can't tell: try anyway */ }
    }

    public static async Task Download(VoicePack pack, HttpClient http, IProgress<VoicePackProgress>? progress, CancellationToken ct)
    {
        CheckSpace(pack);
        long total = pack.Bytes, done = 0;
        foreach (var f in pack.Files)
        {
            var destination = VoicePacks.PathOf(f);
            if (File.Exists(destination) && new FileInfo(destination).Length == f.Bytes) { done += f.Bytes; continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            long before = done;
            await DownloadFile(f, destination, http, b => progress?.Report(new((double)(before + b) / total, before + b, false)), ct).ConfigureAwait(false);
            done += f.Bytes;
            progress?.Report(new((double)done / total, done, false));
        }
        VoicePacks.Forget();
    }

    static async Task DownloadFile(VoicePackFile f, string destination, HttpClient http, Action<long> progress, CancellationToken ct)
    {
        var part = destination + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > f.Bytes) { File.Delete(part); have = 0; }
        if (have < f.Bytes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, f.Url);
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // the server ignored the range
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var buffer = new byte[1 << 20];
            long bytes = have;
            var last = DateTime.MinValue;
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                bytes += n;
                if ((DateTime.UtcNow - last).TotalMilliseconds > 250) { last = DateTime.UtcNow; progress(bytes); }
            }
        }
        string hash;
        await using (var s = File.OpenRead(part)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false));
        if (hash != f.Sha256)
        {
            File.Delete(part);
            throw new IOException($"The download of {System.IO.Path.GetFileName(f.Path)} was damaged (its checksum didn’t match). Try again.");
        }
        File.Move(part, destination, overwrite: true);
    }
}
