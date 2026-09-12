const state = {
  entries: [],
  thumbnails: new Map(),
  selected: null,
  preview: null,
  currentFolder: "",
  busy: false
};

const $ = (id) => document.getElementById(id);

const els = {
  status: $("status"),
  pakPath: $("pakPath"),
  usmapPath: $("usmapPath"),
  aesKey: $("aesKey"),
  openPak: $("openPak"),
  upFolder: $("upFolder"),
  currentPath: $("currentPath"),
  searchText: $("searchText"),
  searchButton: $("searchButton"),
  entryList: $("entryList"),
  previewStage: $("previewStage"),
  selectedPath: $("selectedPath"),
  details: $("details"),
  exportDirectory: $("exportDirectory"),
  exportRaw: $("exportRaw"),
  exportPreview: $("exportPreview"),
  mergePakPath: $("mergePakPath"),
  mergeOutputPath: $("mergeOutputPath"),
  replaceConflicts: $("replaceConflicts"),
  askConflicts: $("askConflicts"),
  oodleCompression: $("oodleCompression"),
  inspectMerge: $("inspectMerge"),
  buildMerge: $("buildMerge"),
  mergeResult: $("mergeResult")
};

function setBusy(busy, text) {
  state.busy = busy;
  document.body.classList.toggle("busy", busy);
  for (const element of document.querySelectorAll("button,input")) {
    element.disabled = busy;
  }
  updateButtons();
  if (text) setStatus(text);
}

function setStatus(text) {
  els.status.textContent = text || "Ready.";
}

function updateButtons() {
  els.openPak.disabled = state.busy || !els.pakPath.value.trim();
  els.upFolder.disabled = state.busy || !state.currentFolder;
  els.searchButton.disabled = state.busy;
  els.exportRaw.disabled = state.busy || !state.selected || state.selected.isDirectory;
  els.exportPreview.disabled = state.busy || !state.selected || !state.preview || !canExportPreview(state.preview);
  els.inspectMerge.disabled = state.busy || !els.pakPath.value.trim() || !els.mergePakPath.value.trim();
  els.buildMerge.disabled = state.busy || !els.pakPath.value.trim() || !els.mergePakPath.value.trim() || !els.mergeOutputPath.value.trim();
}

function canExportPreview(preview) {
  return !!preview.dataUrl || !!preview.text || preview.hasModel;
}

async function fetchJson(url, options) {
  const response = await fetch(url, {
    headers: { "content-type": "application/json" },
    ...options
  });
  const data = await response.json();
  if (!response.ok) {
    throw new Error(data.message || response.statusText);
  }
  return data;
}

async function runBusy(text, action) {
  setBusy(true, text);
  try {
    return await action();
  } catch (error) {
    setStatus(error.message);
    els.details.textContent = String(error.stack || error.message || error);
    throw error;
  } finally {
    setBusy(false);
  }
}

function renderEntries(entries) {
  state.entries = entries || [];
  els.entryList.innerHTML = "";

  if (!state.entries.length) {
    const empty = document.createElement("div");
    empty.className = "empty";
    empty.textContent = "Empty";
    els.entryList.appendChild(empty);
    return;
  }

  for (const entry of state.entries) {
    const row = document.createElement("button");
    row.className = "row";
    row.dataset.path = entry.fullPath;
    if (state.selected?.fullPath === entry.fullPath) row.classList.add("selected");
    row.addEventListener("click", () => openEntry(entry));

    const thumb = document.createElement("div");
    thumb.className = "thumb";
    const cached = state.thumbnails.get(entry.fullPath);
    if (cached) {
      const img = document.createElement("img");
      img.src = cached;
      thumb.appendChild(img);
    } else {
      thumb.textContent = entry.isDirectory ? "DIR" : entry.kind.slice(0, 3).toUpperCase();
    }

    const nameCell = document.createElement("div");
    const name = document.createElement("div");
    name.className = "name";
    name.textContent = entry.name;
    const path = document.createElement("div");
    path.className = "path";
    path.textContent = entry.fullPath;
    nameCell.append(name, path);

    const kind = document.createElement("div");
    kind.className = "kind";
    kind.textContent = entry.kind;

    const size = document.createElement("div");
    size.className = "size";
    size.textContent = entry.isDirectory ? "" : entry.sizeText;

    row.append(thumb, nameCell, kind, size);
    els.entryList.appendChild(row);
  }
}

function setCurrentFolder(folder) {
  state.currentFolder = folder || "";
  els.currentPath.textContent = state.currentFolder ? "/" + state.currentFolder : "/";
}

async function openPak() {
  await runBusy("Opening Pak...", async () => {
    const data = await fetchJson("/api/open", {
      method: "POST",
      body: JSON.stringify({
        pakPath: els.pakPath.value.trim(),
        usmapPath: nullIfBlank(els.usmapPath.value),
        aesKey: nullIfBlank(els.aesKey.value)
      })
    });
    state.selected = null;
    state.preview = null;
    state.thumbnails.clear();
    setCurrentFolder(data.currentFolder);
    renderEntries(data.entries);
    renderPreview(null);
    setStatus(data.status);
  });
}

async function listFolder(folder) {
  await runBusy("Loading...", async () => {
    const data = await fetchJson("/api/list?folder=" + encodeURIComponent(folder || ""));
    setCurrentFolder(data.currentFolder);
    renderEntries(data.entries);
    setStatus(data.status);
  });
}

async function upFolder() {
  const trimmed = state.currentFolder.replace(/\/+$/, "");
  const index = trimmed.lastIndexOf("/");
  await listFolder(index < 0 ? "" : trimmed.slice(0, index + 1));
}

async function search() {
  const query = els.searchText.value.trim();
  if (!query) {
    await listFolder(state.currentFolder);
    return;
  }
  await runBusy("Searching...", async () => {
    const data = await fetchJson("/api/search?q=" + encodeURIComponent(query));
    renderEntries(data.entries);
    setStatus(data.status);
  });
}

async function openEntry(entry) {
  state.selected = entry;
  state.preview = null;
  markSelected(entry.fullPath);
  els.selectedPath.textContent = entry.fullPath;
  els.details.textContent = "";

  if (entry.isDirectory) {
    await listFolder(entry.fullPath);
    return;
  }

  await runBusy("Loading preview...", async () => {
    const preview = await fetchJson("/api/preview?path=" + encodeURIComponent(entry.fullPath));
    state.preview = preview;
    if (preview.kind?.toLowerCase() === "texture" && preview.dataUrl) {
      state.thumbnails.set(entry.fullPath, preview.dataUrl);
      renderEntries(state.entries);
      markSelected(entry.fullPath);
    }
    renderPreview(preview);
    setStatus(preview.status || preview.title);
  });
}

function markSelected(path) {
  for (const row of els.entryList.querySelectorAll(".row")) {
    row.classList.toggle("selected", row.dataset.path === path);
  }
  updateButtons();
}

function renderPreview(preview) {
  els.previewStage.innerHTML = "";
  updateButtons();

  if (!preview) {
    const hint = document.createElement("span");
    hint.className = "preview-hint";
    hint.textContent = "No selection";
    els.previewStage.appendChild(hint);
    els.selectedPath.textContent = "";
    els.details.textContent = "";
    return;
  }

  const kind = (preview.kind || "").toLowerCase();
  if (preview.dataUrl && (kind === "texture" || preview.mimeType?.startsWith("image/"))) {
    const image = document.createElement("img");
    image.src = preview.dataUrl;
    image.alt = preview.title || "preview";
    els.previewStage.appendChild(image);
  } else if (preview.dataUrl && preview.mimeType?.startsWith("audio/")) {
    const audio = document.createElement("audio");
    audio.controls = true;
    audio.src = preview.dataUrl;
    els.previewStage.appendChild(audio);
  } else if (preview.dataUrl && preview.mimeType?.startsWith("video/")) {
    const video = document.createElement("video");
    video.controls = true;
    video.src = preview.dataUrl;
    els.previewStage.appendChild(video);
  } else {
    const hint = document.createElement("span");
    hint.className = "preview-hint";
    hint.textContent = preview.hasModel ? "Model preview data loaded" : preview.kind || "Preview";
    els.previewStage.appendChild(hint);
  }

  els.details.textContent = formatDetails(preview);
}

function formatDetails(preview) {
  const lines = [];
  lines.push(preview.title || "");
  lines.push("Kind: " + (preview.kind || ""));
  if (preview.mimeType) lines.push("Mime: " + preview.mimeType);
  for (const detail of preview.details || []) {
    lines.push(`${detail.label}: ${detail.value}`);
  }
  if (preview.text) {
    lines.push("");
    lines.push(preview.text);
  }
  return lines.join("\n");
}

async function exportRaw() {
  if (!state.selected) return;
  await runBusy("Exporting raw...", async () => {
    const data = await fetchJson("/api/export/raw", {
      method: "POST",
      body: JSON.stringify({
        path: state.selected.fullPath,
        outputDirectory: els.exportDirectory.value.trim()
      })
    });
    setStatus(`Exported ${data.succeeded}, failed ${data.failed}.`);
    if (data.errors?.length) els.details.textContent = data.errors.join("\n");
  });
}

async function exportPreview() {
  if (!state.selected) return;
  await runBusy("Exporting preview...", async () => {
    const data = await fetchJson("/api/export/preview", {
      method: "POST",
      body: JSON.stringify({
        path: state.selected.fullPath,
        outputDirectory: els.exportDirectory.value.trim()
      })
    });
    setStatus(`Exported ${data.files.length} file(s).`);
    els.details.textContent = data.files.join("\n");
  });
}

function mergeRequest() {
  return {
    basePakPath: els.pakPath.value.trim(),
    mergePakPath: els.mergePakPath.value.trim(),
    outputPakPath: nullIfBlank(els.mergeOutputPath.value),
    usmapPath: nullIfBlank(els.usmapPath.value),
    aesKey: nullIfBlank(els.aesKey.value),
    replaceConflicts: els.replaceConflicts.checked,
    useOodleCompression: els.oodleCompression.checked
  };
}

async function inspectMerge() {
  await runBusy("Inspecting merge...", async () => {
    const data = await fetchJson("/api/merge/inspect", {
      method: "POST",
      body: JSON.stringify(mergeRequest())
    });
    renderMergeResult(data);
    setStatus(`Merge inspect: ${data.conflictCount} conflict(s).`);
  });
}

async function buildMerge() {
  await runBusy("Preparing merge...", async () => {
    const request = mergeRequest();
    if (els.askConflicts.checked && request.replaceConflicts) {
      const inspection = await fetchJson("/api/merge/inspect", {
        method: "POST",
        body: JSON.stringify(request)
      });
      renderMergeResult(inspection);
      if (inspection.conflictCount > 0) {
        const ok = confirm(`Found ${inspection.conflictCount} conflict(s). Replace with files from the second Pak?`);
        if (!ok) {
          setStatus("Merge canceled.");
          return;
        }
      }
    }

    setStatus("Building merged Pak...");
    const data = await fetchJson("/api/merge/build", {
      method: "POST",
      body: JSON.stringify(request)
    });
    els.mergeResult.textContent =
      `Output: ${data.outputPakPath}\nFiles: ${data.fileCount}\nConflicts: ${data.conflictCount}\nReplaced: ${data.replacedCount}`;
    setStatus("Merged Pak built.");
  });
}

function renderMergeResult(data) {
  const lines = [
    `Base files: ${data.baseCount}`,
    `Second files: ${data.mergeCount}`,
    `Conflicts: ${data.conflictCount}`
  ];
  if (data.conflicts?.length) {
    lines.push("");
    lines.push(...data.conflicts);
    if (data.conflictCount > data.conflicts.length) lines.push("...");
  }
  els.mergeResult.textContent = lines.join("\n");
}

function nullIfBlank(value) {
  const trimmed = value.trim();
  return trimmed ? trimmed : null;
}

function wire() {
  els.openPak.addEventListener("click", openPak);
  els.upFolder.addEventListener("click", upFolder);
  els.searchButton.addEventListener("click", search);
  els.searchText.addEventListener("keydown", (event) => {
    if (event.key === "Enter") search();
  });
  els.exportRaw.addEventListener("click", exportRaw);
  els.exportPreview.addEventListener("click", exportPreview);
  els.inspectMerge.addEventListener("click", inspectMerge);
  els.buildMerge.addEventListener("click", buildMerge);
  for (const input of document.querySelectorAll("input")) {
    input.addEventListener("input", updateButtons);
  }
  updateButtons();
}

wire();
