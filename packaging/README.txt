Codex Usage Widget {VERSION}
================================

Codex Usage Widget is an unofficial community utility for Windows. It is not
affiliated with or endorsed by OpenAI.

REQUIREMENTS
------------
- Windows
- Codex Desktop installed
- An active Codex sign-in

INSTALL
-------
1. Extract the entire ZIP into a permanent folder.
2. Start Codex Desktop and confirm that you are signed in.
3. Double-click CodexUsageWidget.exe.
4. To start it automatically with Windows, right-click the widget and enable
   "Launch with Windows" after placing it in its permanent folder.

Do not move the executable after enabling Windows startup. If you do move it,
disable and re-enable "Launch with Windows" from the new location.

USING THE WIDGET
----------------
- Drag anywhere on the widget to move it.
- Click X to hide it to the system tray.
- Double-click its tray icon to restore it.
- Right-click the widget or tray icon for additional controls.

LOCAL DATA
----------
The widget creates usage-log.csv beside the executable. It also keeps seven
days of display history and its window position under:

  %LOCALAPPDATA%\CodexUsageWidget

No authentication credentials are copied or stored by the widget.

UNINSTALL
---------
1. Right-click the widget and turn off "Launch with Windows".
2. Exit the widget.
3. Delete the extracted application folder.
4. Optionally delete %LOCALAPPDATA%\CodexUsageWidget to remove local history.

WINDOWS SECURITY NOTICE
-----------------------
This community build is not digitally code-signed, so Windows may identify it
as coming from an unknown publisher. Download releases only from the project's
official GitHub Releases page. The complete source and build script are
available in the repository for inspection.

License: MIT. See LICENSE included in this package.
