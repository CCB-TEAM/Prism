const settingsForm = document.querySelector("#settingsForm");
const settingsMessage = document.querySelector("#settingsMessage");
const statusBadge = document.querySelector("#statusBadge");
const astcVariantSelect = document.querySelector("#astcVariant");

function setStatus(selector, ok) {
    const element = document.querySelector(selector);
    if (!element) return;
    element.textContent = ok ? "正常" : "未配置";
    element.className = ok ? "ok-text" : "error-text";
}

function setMessage(element, text, kind) {
    element.textContent = text;
    element.className = `message ${kind ?? ""}`;
}

// 从路径中提取 astcenc 变体名，例如 "astcenc-avx2" -> "avx2"
function extractVariant(path) {
    if (!path) return "auto";
    const match = path.match(/astcenc-(avx2|sse4\.1|sse2)/i);
    return match ? match[1].toLowerCase() : "auto";
}

async function loadSettings() {
    try {
        const response = await fetch("/api/settings");
        const settings = await response.json();

        setStatus("#bc7Status", settings.bc7Configured);
        setStatus("#astcStatus", settings.astcConfigured);
        setStatus("#cliStatus", settings.cliConfigured);
        statusBadge.textContent = settings.configured ? "后端已就绪" : "后端配置不完整";

        astcVariantSelect.value = extractVariant(settings.astcEncoderPath);

        if (!settings.configured) {
            setMessage(settingsMessage, settings.message || "部分组件未就绪。", "error");
        } else {
            setMessage(settingsMessage, "", "");
        }
    } catch (error) {
        statusBadge.textContent = "连接失败";
        setMessage(settingsMessage, `无法连接到后端: ${error.message}`, "error");
    }
}

settingsForm.addEventListener("submit", async event => {
    event.preventDefault();
    setMessage(settingsMessage, "正在保存...", "");

    const submitButton = settingsForm.querySelector("button[type='submit']");
    submitButton.disabled = true;

    const payload = {
        astcVariant: astcVariantSelect.value,
    };

    try {
        const response = await fetch("/api/settings", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload),
        });

        const result = await response.json();

        if (!response.ok) {
            setMessage(settingsMessage, result.message || "保存失败", "error");
            return;
        }

        setStatus("#bc7Status", result.bc7Configured);
        setStatus("#astcStatus", result.astcConfigured);
        setStatus("#cliStatus", result.cliConfigured);
        statusBadge.textContent = result.configured ? "后端已就绪" : "后端配置不完整";

        setMessage(settingsMessage, result.message || "设置已保存。", "ok");
    } catch (err) {
        setMessage(settingsMessage, `网络错误: ${err.message}`, "error");
    } finally {
        submitButton.disabled = false;
    }
});

loadSettings();
