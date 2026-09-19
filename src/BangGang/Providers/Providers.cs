namespace BangGang;

/// <summary>某个接入商需要的一个参数（对应一个输入框）。</summary>
/// <param name="Key">存储键：url / key / model / appid / secret / secret2 / region …</param>
/// <param name="Label">输入框左侧标题（不同服务商叫法不同）</param>
/// <param name="Desc">标题下的说明文字</param>
/// <param name="Placeholder">输入框占位示例</param>
/// <param name="Secret">是否按密钥处理（带显示/隐藏眼睛）</param>
internal sealed record ProviderField(string Key, string Label, string Desc, string Placeholder, bool Secret = false);

/// <summary>一个接入商预设：名称 + 需要填写的参数列表 + 默认值。</summary>
/// <param name="Guide">
/// 该服务商的**官方接入指引**地址（模型接入页右侧那枚「打开外部链接」图标按钮点开的）。
/// 「自定义」留空 —— 没有官方可指，按钮也就跟着灰掉，这正是「选了非自定义模型才给导航」的实现。
/// </param>
internal sealed record ProviderPreset(
    string Name,
    ProviderField[] Fields,
    Dictionary<string, string> Defaults,
    bool Vision = true,
    string Note = "",
    string Guide = "");

/// <summary>
/// 各接入商的参数表（按官方文档整理）：
///   · 对话：所有主流服务商都提供 OpenAI 兼容接口，因此统一为「地址 + Key + 模型」，
///     但个别服务商的叫法/必填项不同（火山方舟要“推理接入点 ID”、Ollama 本地不需要 Key）。
///   · 语音：只保留火山（API Key + 资源 ID，双向流式二进制帧协议）与讯飞
///     （APPID + APIKey + APISecret，URL 签名 + JSON 帧）两家内置商家，协议互不相同，
///     客户端按服务商名分派（见 <c>SttSession</c>）。
/// “自定义”在对话侧永远排在第一位并作为默认项（语音侧没有自定义档，原因见 Stt 数组注释）。
/// </summary>
internal static class Providers
{
    private static ProviderField Url(string ph, string desc = "接口地址（Base URL）") =>
        new("url", "接口地址", desc, ph);

    private static ProviderField Key(string ph = "sk-…", string desc = "仅保存在本机") =>
        new("key", "API Key", desc, ph, true);

    private static ProviderField Model(string ph, string desc = "服务商提供的模型名") =>
        new("model", "模型名", desc, ph);

    // ---------------- 对话模型 ----------------
    public static readonly ProviderPreset[] Chat =
    {
        new("自定义",
            new[] { Url("https://api.example.com/v1"), Key(), Model("模型名，例如 gpt-4o") },
            new Dictionary<string, string>(), true,
            "任何 OpenAI 兼容接口都可以接入"),

        new("OpenAI",
            new[] { Url("https://api.openai.com/v1"), Key(), Model("gpt-4o") },
            new() { ["url"] = "https://api.openai.com/v1", ["model"] = "gpt-4o" }, true, "",
            "https://platform.openai.com/docs/quickstart"),

        new("DeepSeek",
            new[] { Url("https://api.deepseek.com/v1"), Key(), Model("deepseek-chat") },
            new() { ["url"] = "https://api.deepseek.com/v1", ["model"] = "deepseek-chat" }, false,
            "文本模型，暂不支持图片输入",
            "https://api-docs.deepseek.com/zh-cn/"),

        new("通义千问",
            new[] { Url("https://dashscope.aliyuncs.com/compatible-mode/v1"), Key(), Model("qwen-vl-max") },
            new() { ["url"] = "https://dashscope.aliyuncs.com/compatible-mode/v1", ["model"] = "qwen-vl-max" }, true,
            "DashScope 的 OpenAI 兼容模式",
            "https://help.aliyun.com/zh/model-studio/getting-started/what-is-model-studio"),

        new("Kimi",
            new[] { Url("https://api.moonshot.cn/v1"), Key(), Model("kimi-latest") },
            new() { ["url"] = "https://api.moonshot.cn/v1", ["model"] = "kimi-latest" }, true, "",
            "https://platform.moonshot.cn/docs/guide/start-using-kimi-api"),

        new("火山方舟",
            new[] { Url("https://ark.cn-beijing.volces.com/api/v3"), Key(),
                    new ProviderField("model", "推理接入点 / 模型", "填推理接入点 ID 或模型名", "ep-2024xxxxxx 或 doubao-1.5-vision-pro") },
            new() { ["url"] = "https://ark.cn-beijing.volces.com/api/v3" }, true,
            "豆包大模型，模型名可填推理接入点 ID",
            "https://www.volcengine.com/docs/82379"),

        new("智谱 GLM",
            new[] { Url("https://open.bigmodel.cn/api/paas/v4"), Key(), Model("glm-4-plus") },
            new() { ["url"] = "https://open.bigmodel.cn/api/paas/v4", ["model"] = "glm-4-plus" }, false,
            "glm-4v 系列可开启多模态",
            "https://open.bigmodel.cn/dev/api"),

        new("Ollama（本地）",
            new[] { Url("http://localhost:11434/v1", "本地服务地址"), Model("qwen2.5vl:7b") },
            new() { ["url"] = "http://localhost:11434/v1", ["model"] = "qwen2.5vl:7b" }, true,
            "本地部署，无需 API Key",
            "https://docs.ollama.com/"),
    };

    // ---------------- 实时语音转写 ----------------
    //
    // 只保留火山与讯飞两家 —— 实时转写走的是两家各自的私有 WebSocket 协议
    // （火山是二进制帧 + X-Api-* 头，讯飞是 URL 签名 + JSON 帧），客户端代码按
    // 服务商名分派，因此不存在「自定义」档位：填了地址也没有对应的协议实现。
    // 预设名保持旧称不变：已存的用户档案（SttProfiles）是按名字索引的。
    public static readonly ProviderPreset[] Stt =
    {
        new("火山引擎（流式）",
            new[]
            {
                Url("wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async",
                    "双向流式：bigmodel_async（推荐）/ bigmodel（包进包出）"),
                new ProviderField("key", "API Key", "控制台「API Key 管理」里创建的 Key（新版控制台只需这一项）",
                    "你的 API Key", true),
                new ProviderField("model", "资源 ID",
                    "新版控制台默认 2.0 小时版 volc.seedasr.sauc.duration；1.0 小时版 volc.bigasr.sauc.duration（并发版把 duration 换成 concurrent）",
                    "volc.seedasr.sauc.duration"),
            },
            new()
            {
                ["url"] = "wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async",
                ["model"] = "volc.seedasr.sauc.duration",
            },
            true, "双向流式识别，握手头 X-Api-Key / X-Api-Resource-Id / X-Api-Connect-Id",
            "https://docs.volcengine.com/docs/DoubaoVoice/bidirectional-streaming-automatic-speech-recognition-websocket?lang=zh"),

        new("讯飞（实时转写）",
            new[]
            {
                Url("wss://office-api-ast-dx.iflyaisol.com/ast/communicate/v1"),
                new ProviderField("appid", "APPID", "控制台应用的 APPID", "你的 APPID"),
                new ProviderField("key", "APIKey", "控制台应用的 APIKey", "你的 APIKey", true),
                new ProviderField("secret2", "APISecret", "签名用：signa = HMAC-SHA1(APISecret, MD5(appid+ts))", "你的 APISecret", true),
            },
            new() { ["url"] = "wss://office-api-ast-dx.iflyaisol.com/ast/communicate/v1" },
            true, "星火大模型实时语音转写（中英 + 方言混合识别）",
            "https://www.xfyun.cn/doc/spark/asr_llm/rtasr_llm.html"),
    };
}
