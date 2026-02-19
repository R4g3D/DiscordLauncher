/* global WebSocket */
'use strict';

const { spawn } = require('child_process');

const ACTION_UUID = 'com.kenobi.discordlauncher.launch';
const DEFAULT_TITLE = 'Discord';
const OFFLINE_STATE = 0;
const RUNNING_STATE = 1;
const STATUS_POLL_INTERVAL_MS = 3000;

let websocket = null;
let pluginUUID = null;
const actionContexts = new Set();
let statusPollTimer = null;
let iconLoadPromise = null;
let dynamicImages = {
  normal: null,
  running: null
};

function connectElgatoStreamDeckSocket(inPort, inPluginUUID, inRegisterEvent) {
  pluginUUID = inPluginUUID;
  websocket = new WebSocket(`ws://127.0.0.1:${inPort}`);

  websocket.onopen = () => {
    websocket.send(JSON.stringify({
      event: inRegisterEvent,
      uuid: pluginUUID
    }));
  };

  websocket.onmessage = async (event) => {
    const message = JSON.parse(event.data);

    if (message.event === 'willAppear' && message.action === ACTION_UUID) {
      actionContexts.add(message.context);
      setTitle(message.context, DEFAULT_TITLE);
      await ensureDiscordActionImages();
      applyContextImages(message.context);
      await refreshContextState(message.context);
      ensureStatusPolling();
      return;
    }

    if (message.event === 'willDisappear' && message.action === ACTION_UUID) {
      actionContexts.delete(message.context);
      if (actionContexts.size === 0) {
        stopStatusPolling();
      }
      return;
    }

    if (message.event === 'keyDown' && message.action === ACTION_UUID) {
      await onActionPressed(message.context);
    }
  };
}

async function onActionPressed(context) {
  setTitle(context, 'Working...');

  try {
    await ensureDiscordActionImages();
    applyContextImages(context);

    const focused = await focusDiscordWindow();

    if (focused) {
      setTitle(context, DEFAULT_TITLE);
      await refreshAllContextStates();
      return;
    }

    const launched = await launchDiscordViaUpdater();

    if (!launched) {
      setTitle(context, 'Not Found');
      await refreshAllContextStates();
      return;
    }

    await sleep(1500);
    await focusDiscordWindow();
    setTitle(context, DEFAULT_TITLE);
    await refreshAllContextStates();
  } catch (error) {
    setTitle(context, 'Error');
    log(`Discord launcher failed: ${error.message}`);
    await refreshAllContextStates();
  }
}

async function isDiscordRunning() {
  const statusScript = [
    "$proc = Get-Process -Name 'Discord' -ErrorAction SilentlyContinue | Select-Object -First 1",
    "if ($proc) { Write-Output 'RUNNING'; exit 0 }",
    "Write-Output 'NOT_RUNNING'",
    "exit 1"
  ].join('; ');

  const result = await runPowerShell(statusScript);
  return result.code === 0 && result.stdout.includes('RUNNING');
}

async function ensureDiscordActionImages() {
  if (dynamicImages.normal && dynamicImages.running) {
    return;
  }

  if (!iconLoadPromise) {
    iconLoadPromise = loadDiscordActionImages();
  }

  try {
    await iconLoadPromise;
  } finally {
    iconLoadPromise = null;
  }
}

async function loadDiscordActionImages() {
  const iconScript = `
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Convert-BitmapToDataUrl {
  param([System.Drawing.Bitmap]$Bitmap)
  $memory = New-Object System.IO.MemoryStream
  $Bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
  $base64 = [Convert]::ToBase64String($memory.ToArray())
  $memory.Dispose()
  return ('data:image/png;base64,' + $base64)
}

function New-CanvasBitmap {
  param([int]$Size)
  $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
  $gfx = [System.Drawing.Graphics]::FromImage($bmp)
  $gfx.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $gfx.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $gfx.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $gfx.Clear([System.Drawing.Color]::FromArgb(32, 34, 37))
  return @{ Bitmap = $bmp; Graphics = $gfx }
}

function Resolve-DiscordBinaryPath {
  $appsRoot = Join-Path $env:LOCALAPPDATA 'Discord'
  $discordExe = Get-ChildItem -Path (Join-Path $appsRoot 'app-*') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'Discord.exe' } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1

  if ($discordExe) { return $discordExe }

  $updater = Join-Path $appsRoot 'Update.exe'
  if (Test-Path $updater) { return $updater }

  return $null
}

$binaryPath = Resolve-DiscordBinaryPath
if (-not $binaryPath) {
  throw 'No Discord binary found for icon extraction.'
}

$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($binaryPath)
if (-not $icon) {
  throw 'Failed to extract Discord icon.'
}

$sourceBitmap = $icon.ToBitmap()
$size = 144
$canvas = New-CanvasBitmap -Size $size
$normalBitmap = $canvas.Bitmap
$normalGraphics = $canvas.Graphics
$normalGraphics.DrawImage($sourceBitmap, (New-Object System.Drawing.Rectangle(8, 8, $size - 16, $size - 16)))

$runningBitmap = New-Object System.Drawing.Bitmap($normalBitmap)
$runningGraphics = [System.Drawing.Graphics]::FromImage($runningBitmap)
$runningGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$badgeDiameter = [Math]::Round($size * 0.25)
$badgeInset = [Math]::Round($size * 0.07)
$badgeX = $size - $badgeDiameter - $badgeInset
$badgeY = $size - $badgeDiameter - $badgeInset
$badgeRect = New-Object System.Drawing.Rectangle($badgeX, $badgeY, $badgeDiameter, $badgeDiameter)
$badgeBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(43, 172, 119))
$badgePen = New-Object System.Drawing.Pen([System.Drawing.Color]::Black, [Math]::Max(2, [Math]::Round($size * 0.028)))
$runningGraphics.FillEllipse($badgeBrush, $badgeRect)
$runningGraphics.DrawEllipse($badgePen, $badgeRect)

$result = @{
  normal = (Convert-BitmapToDataUrl -Bitmap $normalBitmap)
  running = (Convert-BitmapToDataUrl -Bitmap $runningBitmap)
} | ConvertTo-Json -Compress

$badgePen.Dispose()
$badgeBrush.Dispose()
$runningGraphics.Dispose()
$runningBitmap.Dispose()
$normalGraphics.Dispose()
$normalBitmap.Dispose()
$sourceBitmap.Dispose()
$icon.Dispose()

Write-Output $result
`

  const result = await runPowerShell(iconScript, 20000);
  if (result.code !== 0 || !result.stdout.trim()) {
    throw new Error('Could not load Discord icon images.');
  }

  const parsed = JSON.parse(result.stdout.trim());
  if (!parsed.normal || !parsed.running) {
    throw new Error('Discord icon image payload is invalid.');
  }

  dynamicImages = {
    normal: parsed.normal,
    running: parsed.running
  };

  Array.from(actionContexts).forEach((context) => applyContextImages(context));
}

async function refreshAllContextStates() {
  const contexts = Array.from(actionContexts);
  if (contexts.length === 0) {
    return;
  }

  const running = await isDiscordRunning();
  const state = running ? RUNNING_STATE : OFFLINE_STATE;
  contexts.forEach((context) => setState(context, state));
}

async function refreshContextState(context) {
  const running = await isDiscordRunning();
  setState(context, running ? RUNNING_STATE : OFFLINE_STATE);
}

function ensureStatusPolling() {
  if (statusPollTimer) {
    return;
  }

  statusPollTimer = setInterval(() => {
    refreshAllContextStates().catch((error) => {
      log(`Status refresh failed: ${error.message}`);
    });
  }, STATUS_POLL_INTERVAL_MS);
}

function stopStatusPolling() {
  if (!statusPollTimer) {
    return;
  }

  clearInterval(statusPollTimer);
  statusPollTimer = null;
}

async function focusDiscordWindow() {
  const focusScript = [
    "$proc = Get-Process -Name 'Discord' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1",
    "if (-not $proc) {",
    "  $proc = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like '*Discord*' -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1",
    "}",
    "if (-not $proc) { Write-Output 'NOT_FOUND'; exit 1 }",
    "if (-not ('DiscordWindowInterop' -as [type])) {",
    "  Add-Type @\"",
    "using System;",
    "using System.Runtime.InteropServices;",
    "public static class DiscordWindowInterop {",
    "  [DllImport(\"user32.dll\")] public static extern bool SetForegroundWindow(IntPtr hWnd);",
    "  [DllImport(\"user32.dll\")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);",
    "}",
    "\"@",
    "}",
    "[void][DiscordWindowInterop]::ShowWindowAsync($proc.MainWindowHandle, 9)",
    "Start-Sleep -Milliseconds 120",
    "[void][DiscordWindowInterop]::SetForegroundWindow($proc.MainWindowHandle)",
    "Write-Output 'FOUND'",
    "exit 0"
  ].join('; ');

  const result = await runPowerShell(focusScript);
  return result.code === 0 && result.stdout.includes('FOUND');
}

async function launchDiscordViaUpdater() {
  const launchScript = [
    "$updatePath = Join-Path $env:LOCALAPPDATA 'Discord\\Update.exe'",
    "if (-not (Test-Path $updatePath)) { Write-Output 'MISSING'; exit 1 }",
    "Start-Process -FilePath $updatePath -ArgumentList '--processStart','Discord.exe' -WindowStyle Hidden",
    "Write-Output 'LAUNCHED'",
    "exit 0"
  ].join('; ');

  const result = await runPowerShell(launchScript);
  return result.code === 0 && result.stdout.includes('LAUNCHED');
}

function runPowerShell(command, timeoutMs = 10000) {
  return new Promise((resolve, reject) => {
    const child = spawn('powershell.exe', [
      '-NoProfile',
      '-ExecutionPolicy', 'Bypass',
      '-Command', command
    ], {
      windowsHide: true
    });

    let stdout = '';
    let stderr = '';

    const timer = setTimeout(() => {
      child.kill();
      reject(new Error('PowerShell command timed out.'));
    }, timeoutMs);

    child.stdout.on('data', (data) => {
      stdout += data.toString();
    });

    child.stderr.on('data', (data) => {
      stderr += data.toString();
    });

    child.on('error', (err) => {
      clearTimeout(timer);
      reject(err);
    });

    child.on('close', (code) => {
      clearTimeout(timer);
      if (stderr.trim()) {
        log(stderr.trim());
      }
      resolve({ code, stdout, stderr });
    });
  });
}

function setTitle(context, title) {
  if (!websocket) {
    return;
  }

  websocket.send(JSON.stringify({
    event: 'setTitle',
    context,
    payload: {
      title,
      target: 0
    }
  }));
}

function applyContextImages(context) {
  if (!dynamicImages.normal || !dynamicImages.running) {
    return;
  }

  setImage(context, dynamicImages.normal, OFFLINE_STATE);
  setImage(context, dynamicImages.running, RUNNING_STATE);
}

function setState(context, state) {
  if (!websocket) {
    return;
  }

  websocket.send(JSON.stringify({
    event: 'setState',
    context,
    payload: {
      state
    }
  }));
}

function setImage(context, image, state) {
  if (!websocket || !image) {
    return;
  }

  websocket.send(JSON.stringify({
    event: 'setImage',
    context,
    payload: {
      image,
      target: 0,
      state
    }
  }));
}

function log(message) {
  if (!websocket || !pluginUUID) {
    return;
  }

  websocket.send(JSON.stringify({
    event: 'logMessage',
    payload: {
      message: `[DiscordLauncher] ${message}`
    }
  }));
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

window.connectElgatoStreamDeckSocket = connectElgatoStreamDeckSocket;

