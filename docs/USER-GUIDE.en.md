# AIMaster User Guide

[简体中文](USER-GUIDE.md) · **English**

![AIMaster dashboard in English](images/dashboard-en.png)

## Main dashboard

The dashboard contains seven card types:

1. **Primary quota runway** shows remaining quota, usage, and the next reset.
2. **Task guard** shows protection status and can pause tasks visible to the current connection.
3. **Quota windows** lists the weekly and short windows returned by the account.
4. **Usage forecast** charts estimated and projected weekly quota remaining, plus the last seven days of usage.
5. **Recent tasks** lists running, waiting, interrupted, paused, and completed tasks.
6. **Local usage** totals this week's local tokens and estimates weekly-quota attribution by project.
7. **Guard policy** configures warning and pause thresholds, automatic pausing, and floating-window text size.

Select **Sync now** to reconnect to Codex and refresh all data.

### Card layout

- Drag a card header to reorder it or move it between columns.
- Select the arrow in the upper-right corner to collapse or expand a card.
- Drag the diagonal handle in the lower-right corner to resize a card.
- Double-click the resize handle to restore its adaptive size.
- Card order, collapsed state, and custom size are saved automatically.

## Guard policy

- **Warning** changes the dashboard to a warning state when usage reaches the selected percentage.
- **Pause limit** activates the blocking state at the selected percentage.
- **Automatic pause** attempts to interrupt running tasks visible to the current App Server connection.
- **Floating font size** adjusts overlay text from 100% to 150%.

Live control is scoped to the App Server connection. Tasks running in another Codex window may need to be stopped in that window.

## Floating monitor

![AIMaster floating monitor in English](images/floating-en.png)

Select **Floating monitor** in the main window. The overlay stays on top and displays:

- current status and task name;
- quota remaining;
- active model and reasoning effort;
- up to three recent tasks.

The main window appears in the Windows taskbar; the floating monitor does not. Closing the main window hides AIMaster to the system tray while monitoring continues.

### System tray

- Click or double-click the AIMaster tray icon to restore the main window.
- Select **Show main window** to restore the dashboard.
- Select **AI floating monitor** to open or activate the overlay.
- Select **Exit** to close every window and stop the background process.

If the tray icon is not visible, expand the hidden-icons area in the Windows taskbar.

### Edge auto-hide

Drag the overlay to the top, left, or right edge of the current monitor. It collapses to an 8 px hover strip and pauses refreshing. Hover over the strip to expand and refresh immediately. Docking uses the work area of the monitor containing the overlay and works independently on secondary displays.

### Context menu

- **Refresh now** reads the latest state immediately.
- **Collapse now / Expand window** switches the docked state manually.
- **Auto-hide at edge** controls automatic collapsing near a screen edge.
- **Task name** chooses between the conversation title and the latest user request.
- **Refresh interval** offers 1, 2, 5, 10, or 30 seconds.

Conversation titles work well as stable identifiers. The latest-request mode is useful when following what a long-running task is currently doing. AIMaster falls back to the other available name if the selected source is missing.

## Language switching

Select `EN` or `中文` in the upper-right corner of the main window. The dashboard, floating monitor, tray menu, status text, and reset times switch immediately, and the choice is saved locally.

![AIMaster 中文主面板](images/dashboard-zh.png)

## Status reference

| Status | Meaning |
|---|---|
| Running | The task is still executing or its latest local session has no completion marker. |
| Waiting for approval/input | The task requires action before it can continue. |
| Interrupted/paused | The task was interrupted, cancelled, or paused. |
| Completed | The task finished normally. |
| Offline | App Server is unavailable. Quota may not update, but local task records are still used when possible. |

## Settings and migration

Settings are stored in `%LOCALAPPDATA%\AIMaster\settings.json`.

If `%LOCALAPPDATA%\SoftwareToolkit\ai-manager.json` exists and no AIMaster settings file has been created yet, it is copied automatically on first launch.
