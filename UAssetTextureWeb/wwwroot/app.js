const statusBadge = document.querySelector("#statusBadge");
const replacePanel = document.querySelector("#replace");
const packPanel = document.querySelector("#pack");
const replaceForm = document.querySelector("#replaceForm");
const packForm = document.querySelector("#packForm");
const replaceMessage = document.querySelector("#replaceMessage");
const packMessage = document.querySelector("#packMessage");
const serverMessage = document.querySelector("#serverMessage");
const downloadLink = document.querySelector("#downloadLink");

let currentJob = null;

// 从 sessionStorage 恢复上次任务信息
function restoreLastJob() {
    const savedJobId = sessionStorage.getItem("lastJobId");
    if (savedJobId) {
        currentJob = {
            jobId: savedJobId,
            downloadUrl: `/api/jobs/${savedJobId}/download`,
            uassetFileName: "未知",
            uexpFileName: "未知",
            ubulkFileName: null
        };
        downloadLink.href = currentJob.downloadUrl;
        downloadLink.classList.remove("hidden");
        setMessage(replaceMessage, "已恢复上次替换任务，可直接下载或打包。", "ok");
    }
}

async function loadSettings() {
    try {
        const response = await fetch("/api/settings");
        const settings = await response.json();

        setStatus("#bc7Status", settings.bc7Configured);
        setStatus("#astcStatus", settings.astcConfigured);
        setStatus("#cliStatus", settings.cliConfigured);
        document.querySelector("#parallelStatus").textContent = `${settings.maxParallelJobs} 个`;
        statusBadge.textContent = settings.configured ? "后端已就绪" : "后端配置不完整";
        replacePanel.classList.toggle("hidden", !settings.configured);
        packPanel.classList.toggle("hidden", !settings.configured);
        setMessage(serverMessage, settings.message ?? "", settings.configured ? "ok" : "error");
    } catch (error) {
        statusBadge.textContent = "配置检查失败";
        setMessage(serverMessage, error.message, "error");
        console.error("加载设置失败:", error);
    }
}

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

// 提交替换
replaceForm.addEventListener("submit", async event => {
    event.preventDefault();
    setMessage(replaceMessage, "正在处理，后端会自动识别原资源格式...", "");
    downloadLink.classList.add("hidden");
    currentJob = null;
    sessionStorage.removeItem("lastJobId");

    const submitButton = replaceForm.querySelector("button[type='submit']");
    submitButton.disabled = true;

    try {
        const response = await fetch("/api/replace", {
            method: "POST",
            body: new FormData(replaceForm),
        });

        if (!response.ok) {
            const error = await response.json().catch(() => ({ message: "替换请求失败" }));
            setMessage(replaceMessage, error.message ?? "服务器错误", "error");
            return;
        }

        const result = await response.json();
        currentJob = result;
        sessionStorage.setItem("lastJobId", result.jobId);

        downloadLink.href = result.downloadUrl;
        downloadLink.classList.remove("hidden");
        fillPakPathDefaults(result);

        let msg = "替换完成。";
        if (result.selectedFormat && result.selectedFormat !== result.format) {
            msg += ` 资源实际格式为 ${result.format}（您选择的 ${result.selectedFormat} 已忽略）。`;
        } else {
            msg += ` 已按 ${result.format} 处理。`;
        }
        setMessage(replaceMessage, msg, "ok");
    } catch (err) {
        setMessage(replaceMessage, "网络错误，请检查连接后重试。", "error");
        console.error("替换失败:", err);
    } finally {
        submitButton.disabled = false;
    }
});

// 提交打包（直接下载二进制文件）
packForm.addEventListener("submit", async event => {
    event.preventDefault();
    if (!currentJob) {
        setMessage(packMessage, "请先完成一次纹理替换。", "error");
        return;
    }

    setMessage(packMessage, "正在打包本次修改结果...", "");
    const submitButton = packForm.querySelector("button[type='submit']");
    submitButton.disabled = true;

    const data = new FormData(packForm);
    const payload = {
        outputPakName: data.get("outputPakName"),
        mountPoint: data.get("mountPoint"),
        uassetPakPath: data.get("uassetPakPath"),
        uexpPakPath: data.get("uexpPakPath"),
        ubulkPakPath: data.get("ubulkPakPath"),
        version: data.get("version"),
        useCompression: data.get("useCompression") === "on",
        compression: data.get("compression"),
    };

    try {
        const response = await fetch(`/api/jobs/${currentJob.jobId}/pack`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload),
        });

        if (!response.ok) {
            const error = await response.json().catch(() => ({ message: "打包失败" }));
            setMessage(packMessage, error.message, "error");
            return;
        }

        // 成功时下载文件
        const blob = await response.blob();
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = payload.outputPakName || "patched.pak";
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);

        setMessage(packMessage, "打包完成，文件已开始下载。", "ok");
    } catch (err) {
        setMessage(packMessage, "网络错误或打包失败，请重试。", "error");
        console.error("打包失败:", err);
    } finally {
        submitButton.disabled = false;
    }
});

function fillPakPathDefaults(result) {
    const base = result.uassetFileName.replace(/\.patched\.uasset$/i, "").replace(/\.uasset$/i, "");
    packForm.elements.uassetPakPath.value = `Content/${base}.uasset`;
    packForm.elements.uexpPakPath.value = `Content/${base}.uexp`;
    packForm.elements.ubulkPakPath.value = result.ubulkFileName ? `Content/${base}.ubulk` : "";
}

// 页面初始化
restoreLastJob();
loadSettings();