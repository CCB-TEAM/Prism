using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using AndroidUri = Android.Net.Uri;
using Android.OS;
using AndroidX.Core.Content;
using Avalonia.Android;

namespace Prism.Desktop.Android;

[Activity(
    Label = "Prism",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private MediaPlayer? _audioPlayer;
    private string? _currentAudioPath;
    private bool _audioPrepared;
    private System.Threading.Timer? _audioPositionTimer;

    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;

        // 手机上没有 ffmpeg：M4A / AAC / FLAC 这类素材交给系统解码器
        UAssetAudio.Core.AudioSourceDecoder.PlatformDecoder = AndroidAudioDecoder.TryDecode;
    }

    protected override void OnResume()
    {
        base.OnResume();
        // App 初始化完成后注入原生能力：合并冲突确认对话框、系统分享、内置音频播放器。
        if (global::Avalonia.Application.Current is Prism.Desktop.App app && app.MainVm is not null)
        {
            app.MainVm.NativeConfirmAsync = ShowConfirmDialogAsync;
            app.MainVm.NativeShareFilesAsync = ShareFilesAsync;
            app.MainVm.NativeAudioPlayAsync = PlayAudioAsync;
            app.MainVm.NativeAudioStopAsync = StopAudioAsync;
            app.MainVm.NativeAudioSeekAsync = SeekAudioAsync;
            app.MainVm.NativeAudioSetPausedAsync = SetAudioPausedAsync;
            app.MainVm.NativeUpdateDialogAsync = ShowUpdateDialogAsync;
            app.MainVm.NativeInstallApkAsync = InstallApkAsync;
        }
    }

    protected override void OnDestroy()
    {
        if (Current == this)
        {
            Current = null;
        }

        StopAudioCore();
        if (global::Avalonia.Application.Current is Prism.Desktop.App app && app.MainVm is not null)
        {
            app.MainVm.NativeConfirmAsync = null;
            app.MainVm.NativeShareFilesAsync = null;
            app.MainVm.NativeAudioPlayAsync = null;
            app.MainVm.NativeAudioStopAsync = null;
            app.MainVm.NativeAudioSeekAsync = null;
            app.MainVm.NativeAudioSetPausedAsync = null;
            app.MainVm.NativeUpdateDialogAsync = null;
            app.MainVm.NativeInstallApkAsync = null;
        }

        base.OnDestroy();
    }

    /// <summary>
    /// 使用 MediaPlayer 的内置音频播放器：异步 Prepare，不阻塞 UI；
    /// 定时回传进度供共享 UI 显示播放条，支持拖动定位。
    /// </summary>
    private Task PlayAudioAsync(string path, double startPositionSeconds)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            try
            {
                StopAudioCore();
                _currentAudioPath = path;
                var player = new MediaPlayer();
                _audioPlayer = player;
                player.SetDataSource(path);

                string playingPath = path;
                player.Prepared += (_, _) =>
                {
                    if (!ReferenceEquals(_audioPlayer, player) || !string.Equals(_currentAudioPath, playingPath, StringComparison.Ordinal))
                    {
                        return;
                    }

                    try
                    {
                        if (startPositionSeconds > 0)
                        {
                            player.SeekTo((int)Math.Clamp(startPositionSeconds * 1000, 0, player.Duration));
                        }

                        player.Start();
                        _audioPrepared = true;
                        StartAudioPositionTimer();
                        double duration = Math.Max(0, player.Duration / 1000.0);
                        if (global::Avalonia.Application.Current is Prism.Desktop.App app)
                        {
                            app.MainVm?.UpdateAudioPlaybackState(startPositionSeconds, duration);
                        }

                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                };

                player.Completion += (_, _) =>
                {
                    if (!string.Equals(_currentAudioPath, playingPath, StringComparison.Ordinal))
                    {
                        return;
                    }

                    StopAudioPositionTimer();
                    _audioPrepared = false;
                    _currentAudioPath = null;
                    if (global::Avalonia.Application.Current is Prism.Desktop.App completionApp)
                    {
                        completionApp.MainVm?.NotifyNativeAudioPlaybackCompleted();
                    }
                };

                player.Error += (_, args) =>
                {
                    if (ReferenceEquals(_audioPlayer, player) && !tcs.Task.IsCompleted)
                    {
                        tcs.TrySetException(new InvalidOperationException($"MediaPlayer error: {args.What}"));
                    }
                };

                // PrepareAsync 避免大 WAV 在 UI 线程同步解码造成卡顿。
                player.PrepareAsync();
            }
            catch (Exception ex)
            {
                StopAudioCore();
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private Task StopAudioAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            StopAudioCore();
            tcs.TrySetResult(true);
        });
        return tcs.Task;
    }

    private Task SetAudioPausedAsync(bool paused)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            try
            {
                MediaPlayer? player = _audioPlayer;
                if (player is null || !_audioPrepared)
                {
                    tcs.TrySetResult(true);
                    return;
                }

                if (paused)
                {
                    if (player.IsPlaying)
                    {
                        player.Pause();
                    }

                    StopAudioPositionTimer();
                }
                else
                {
                    if (!player.IsPlaying)
                    {
                        player.Start();
                    }

                    StartAudioPositionTimer();
                }

                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private Task SeekAudioAsync(double seconds)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            try
            {
                MediaPlayer? player = _audioPlayer;
                if (player is not null && _audioPrepared)
                {
                    int position = (int)Math.Clamp(seconds * 1000, 0, player.Duration);
                    player.SeekTo(position);
                    if (global::Avalonia.Application.Current is Prism.Desktop.App app)
                    {
                        app.MainVm?.UpdateAudioPlaybackState(position / 1000.0, player.Duration / 1000.0);
                    }
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                return;
            }

            tcs.TrySetResult(true);
        });
        return tcs.Task;
    }

    private void StartAudioPositionTimer()
    {
        StopAudioPositionTimer();
        _audioPositionTimer = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    MediaPlayer? player = _audioPlayer;
                    if (player is null || !_audioPrepared)
                    {
                        return;
                    }

                    double position = player.CurrentPosition / 1000.0;
                    double duration = player.Duration / 1000.0;
                    RunOnUiThread(() =>
                    {
                        if (global::Avalonia.Application.Current is Prism.Desktop.App app)
                        {
                            app.MainVm?.UpdateAudioPlaybackState(position, duration);
                        }
                    });
                }
                catch
                {
                    // 播放器状态切换瞬间读取失败可忽略。
                }
            },
            null,
            250,
            500);
    }

    private void StopAudioPositionTimer()
    {
        _audioPositionTimer?.Dispose();
        _audioPositionTimer = null;
    }

    private void StopAudioCore()
    {
        StopAudioPositionTimer();
        _audioPrepared = false;
        try
        {
            _audioPlayer?.Stop();
            _audioPlayer?.Release();
        }
        catch
        {
            // 播放器可能已经释放。
        }

        _audioPlayer = null;
        _currentAudioPath = null;
    }

    private Task<bool> ShowConfirmDialogAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        RunOnUiThread(() =>
        {
            new AlertDialog.Builder(this)
                .SetTitle(title)
                .SetMessage(message)
                .SetPositiveButton("确定", (_, _) => tcs.TrySetResult(true))
                .SetNegativeButton("取消", (_, _) => tcs.TrySetResult(false))
                .SetCancelable(false)
                .Show();
        });
        return tcs.Task;
    }

    /// <summary>
    /// Android 系统分享：把导出的临时文件复制到 FileProvider 授权的 cache 目录，
    /// 然后打开 ACTION_SEND / ACTION_SEND_MULTIPLE，文件不再需要用户去文件管理器里找。
    /// </summary>
    private Task ShareFilesAsync(IReadOnlyList<string> paths, string title)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (paths.Count == 0)
        {
            tcs.TrySetException(new InvalidOperationException("没有可分享的文件。"));
            return tcs.Task;
        }

        string authority = $"{PackageName}.fileprovider";
        Java.IO.File shareDir = new(CacheDir, "shared");
        _ = Task.Run(() =>
        {
            try
            {
                var prepared = PrepareShareFiles(shareDir, paths, authority);
                RunOnUiThread(() =>
                {
                    try
                    {
                        StartActivity(BuildShareIntent(prepared, title));
                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static List<(AndroidUri Uri, string MimeType)> PrepareShareFiles(
        Java.IO.File shareDir,
        IReadOnlyList<string> paths,
        string authority)
    {
        if (!shareDir.Exists())
        {
            shareDir.Mkdirs();
        }

        // 清理 24 小时前的分享临时文件，既避免缓存膨胀，又不会破坏刚发出的分享 URI。
        long cutoff = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
        foreach (Java.IO.File oldFile in shareDir.ListFiles() ?? [])
        {
            try
            {
                if (oldFile.LastModified() < cutoff)
                {
                    oldFile.Delete();
                }
            }
            catch
            {
                // 忽略单个文件清理失败。
            }
        }

        var result = new List<(AndroidUri Uri, string MimeType)>();
        foreach (string path in paths)
        {
            if (!System.IO.File.Exists(path))
            {
                continue;
            }

            string extension = System.IO.Path.GetExtension(path);
            string shareName = $"{Guid.NewGuid():N}{extension}";
            string sharePath = System.IO.Path.Combine(shareDir.AbsolutePath, shareName);
            System.IO.File.Copy(path, sharePath, overwrite: true);
            Java.IO.File shareFile = new(sharePath);
            AndroidUri uri = FileProvider.GetUriForFile(
                global::Android.App.Application.Context,
                authority,
                shareFile);
            result.Add((uri, GetShareMimeType(extension)));
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException("分享文件准备失败。");
        }

        return result;
    }

    private static Intent BuildShareIntent(List<(AndroidUri Uri, string MimeType)> files, string title)
    {
        Intent intent;
        if (files.Count == 1)
        {
            intent = new Intent(Intent.ActionSend);
            intent.SetType(files[0].MimeType);
            intent.PutExtra(Intent.ExtraStream, files[0].Uri);
        }
        else
        {
            intent = new Intent(Intent.ActionSendMultiple);
            string[] distinctMimeTypes = files.Select(file => file.MimeType).Distinct().ToArray();
            intent.SetType(distinctMimeTypes.Length == 1 ? distinctMimeTypes[0] : "*/*");
            var uris = new List<IParcelable>();
            foreach ((AndroidUri uri, _) in files)
            {
                uris.Add(uri);
            }

            intent.PutParcelableArrayListExtra(Intent.ExtraStream, uris);
        }

        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        intent.PutExtra(Intent.ExtraSubject, title);
        return Intent.CreateChooser(intent, title);
    }

    private static string GetShareMimeType(string? extension) =>
        (extension ?? string.Empty).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".tga" => "image/x-tga",
            ".wav" => "audio/wav",
            ".ogg" or ".oga" or ".opus" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".glb" => "model/gltf-binary",
            ".gltf" => "model/gltf+json",
            ".fbx" => "application/octet-stream",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".pak" or ".utoc" or ".ucas" => "application/octet-stream",
            _ => "application/octet-stream"
        };

    /// <summary>
    /// 「发现新版本」弹窗：标题 + 更新内容摘要 + 「立即更新」/「稍后」。
    /// 用原生 AlertDialog 而不是应用内浮层 —— 用户可能正停在别的页面，
    /// 系统的对话框才盖得住、也才在返回键上表现正常（可跳过）。
    /// </summary>
    private Task<bool> ShowUpdateDialogAsync(string title, string message, string positive, string negative)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            try
            {
                new AlertDialog.Builder(this)
                    .SetTitle(title)
                    .SetMessage(message)
                    .SetPositiveButton(positive, (_, _) => tcs.TrySetResult(true))
                    .SetNegativeButton(negative, (_, _) => tcs.TrySetResult(false))
                    .SetOnCancelListener(new DialogCancelListener(() => tcs.TrySetResult(false)))
                    .Show();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>取消回调（点外部 / 返回键）→ 当作"稍后"。</summary>
    private sealed class DialogCancelListener : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        private readonly Action _onCancel;

        public DialogCancelListener(Action onCancel) => _onCancel = onCancel;

        public void OnCancel(IDialogInterface? dialog) => _onCancel();
    }

    /// <summary>
    /// 把下载好的 APK 交给系统安装器。
    ///
    /// 关键点：APK 必须先落到 FileProvider 授权的目录（cache/shared），
    /// 再用 content:// URI + GrantReadUriPermission 启动，否则 Android 8+ 会拒绝安装。
    /// 另外 Android 8+ 还需要「安装未知来源应用」权限 —— 没有时跳系统设置页让用户开。
    /// </summary>
    private Task InstallApkAsync(string apkPath)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!System.IO.File.Exists(apkPath))
        {
            tcs.TrySetException(new InvalidOperationException("安装包不存在：" + apkPath));
            return tcs.Task;
        }

        string authority = $"{PackageName}.fileprovider";
        Java.IO.File shareDir = new(CacheDir, "shared");
        _ = Task.Run(() =>
        {
            try
            {
                if (!shareDir.Exists())
                {
                    shareDir.Mkdirs();
                }

                string target = System.IO.Path.Combine(shareDir.AbsolutePath, System.IO.Path.GetFileName(apkPath));
                System.IO.File.Copy(apkPath, target, overwrite: true);
                Java.IO.File apkFile = new(target);
                AndroidUri uri = FileProvider.GetUriForFile(
                    global::Android.App.Application.Context,
                    authority,
                    apkFile);

                RunOnUiThread(() =>
                {
                    try
                    {
                        // Android 8+ 需要用户授权"安装未知应用"，没有就直接把设置页打开
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && !PackageManager!.CanRequestPackageInstalls())
                        {
                            try
                            {
                                // 命名空间必须从 global 起：本程序集叫 Prism.Desktop.Android，
                                // 直接写 Android.Provider.* 会被解析成 Prism.Desktop.Android.Provider。
                                var settings = new Intent(
                                    global::Android.Provider.Settings.ActionManageUnknownAppSources,
                                    AndroidUri.Parse("package:" + PackageName));
                                StartActivity(settings);
                            }
                            catch
                            {
                                // 个别 ROM 没有这个页面，继续尝试直接安装
                            }
                        }

                        var intent = new Intent(Intent.ActionView);
                        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
                        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                        intent.AddFlags(ActivityFlags.NewTask);
                        StartActivity(intent);
                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>
    /// 返回键按页面层级返回：子目录→上级目录，其他页面→主页，主页才退出。
    /// </summary>
    public override void OnBackPressed()
    {
        if (global::Avalonia.Application.Current is Prism.Desktop.App app
            && app.MainVm?.HandleBack() == true)
        {
            return;
        }

        base.OnBackPressed();
    }
}
