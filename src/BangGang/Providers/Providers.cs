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
///   · 语音：实时流式接口差异较大（火山引擎要 App ID + Access Token + 资源 ID；
///     讯飞要 APPID + APIKey + APISecret；阿里云要 AppKey + Token；腾讯云要 AppID + SecretId + SecretKey），
///     因此每个服务商的输入框数量与标题都不同。
/// “自定义”永远排在第一位并作为默认项。
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
    public static readonly ProviderPreset[] Stt =
    {
        new("自定义",
            new[]
            {
                Url("wss://…（实时流式接口）"),
                new ProviderField("appid", "App ID", "部分服务商鉴权需要，可留空", "App ID / 账号"),
                new ProviderField("key", "Access Token", "密钥 / Token，仅保存在本机", "Access Token / API Key", true),
                new ProviderField("model", "模型 / 资源 ID", "服务商的模型或资源 ID", "例如 volc.bigasr.sauc.duration"),
            },
            new Dictionary<string, string>()),

        new("火山引擎（流式）",
            new[]
            {
                Url("wss://openspeech.bytedance.com/api/v3/sauc/bigmodel"),
                new ProviderField("appid", "App ID", "控制台应用的 App ID", "你的 App ID"),
                new ProviderField("key", "Access Token", "控制台应用的 Access Token", "你的 Access Token", true),
                new ProviderField("model", "资源 ID", "流式大模型识别资源", "volc.bigasr.sauc.duration"),
            },
            new()
            {
                ["url"] = "wss://openspeech.bytedance.com/api/v3/sauc/bigmodel",
                ["model"] = "volc.bigasr.sauc.duration",
            },
            true, "对应请求头 X-Api-App-Key / X-Api-Access-Key / X-Api-Resource-Id",
            "https://www.volcengine.com/docs/6561"),

        new("讯飞（实时转写）",
            new[]
            {
                Url("wss://iat-api.xfyun.cn/v2/iat"),
                new ProviderField("appid", "APPID", "控制台应用的 APPID", "你的 APPID"),
                new ProviderField("key", "API Key", "控制台应用的 APIKey", "你的 APIKey", true),
                new ProviderField("secret2", "API Secret", "用于生成鉴权签名", "你的 APISecret", true),
            },
            new() { ["url"] = "wss://iat-api.xfyun.cn/v2/iat" },
            true, "三项凭证都来自讯飞开放平台控制台",
            "https://www.xfyun.cn/doc/asr/rtasr/API.html"),

        new("阿里云（实时）",
            new[]
            {
                Url("wss://nls-gateway-cn-shanghai.aliyuncs.com/ws/v1"),
                new ProviderField("appid", "AppKey", "智能语音交互项目的 AppKey", "你的 AppKey"),
                new ProviderField("key", "Token", "由 AccessKey 换取的有效 Token", "你的 Token", true),
                new ProviderField("model", "模型", "paraformer 实时模型", "paraformer-realtime-v2"),
            },
            new()
            {
                ["url"] = "wss://nls-gateway-cn-shanghai.aliyuncs.com/ws/v1",
                ["model"] = "paraformer-realtime-v2",
            },
            true, "不同地域的网关地址不同，可按需替换",
            "https://help.aliyun.com/zh/isi/developer-reference/overview-of-real-time-speech-recognition"),

        new("腾讯云（实时）",
            new[]
            {
                Url("wss://asr.cloud.tencent.com/asr/v2/"),
                new ProviderField("appid", "AppID", "腾讯云账号 AppID", "你的 AppID"),
                new ProviderField("key", "SecretId", "访问密钥 SecretId", "你的 SecretId", true),
                new ProviderField("secret2", "SecretKey", "访问密钥 SecretKey", "你的 SecretKey", true),
            },
            new() { ["url"] = "wss://asr.cloud.tencent.com/asr/v2/" },
            true, "地址末尾需拼接 AppID",
            "https://cloud.tencent.com/document/product/1093/48982"),

        new("Deepgram",
            new[]
            {
                Url("wss://api.deepgram.com/v1/listen"),
                new ProviderField("key", "API Key", "Deepgram 控制台创建", "你的 API Key", true),
                new ProviderField("model", "模型", "实时听写模型", "nova-3"),
            },
            new() { ["url"] = "wss://api.deepgram.com/v1/listen", ["model"] = "nova-3" },
            true, "地址可带参数，如 ?language=zh",
            "https://developers.deepgram.com/docs/streaming"),

        new("OpenAI",
            new[]
            {
                Url("https://api.openai.com/v1"),
                Key(),
                new ProviderField("model", "模型", "转写模型（非流式）", "whisper-1"),
            },
            new() { ["url"] = "https://api.openai.com/v1", ["model"] = "whisper-1" },
            true, "Whisper 为整段转写，不是实时流式",
            "https://platform.openai.com/docs/guides/speech-to-text"),
    };
}
