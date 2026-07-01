using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows;
using System.IO;

namespace SSHEasyApp.Controls
{
    public class MstscHost : HwndHost
    {
        private const int GWL_STYLE = -16;
        private const int WS_CHILD = 0x40000000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_VISIBLE = 0x10000000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName, 
            int dwStyle, int x, int y, int nWidth, int nHeight, 
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        private Process? _process;
        private IntPtr _childHandle;
        private IntPtr _parentHandle;

        public event Action? ProcessExited;

        public MstscHost() { }

        public void StartProcess(string args)
        {
            _process = new Process();
            _process.StartInfo.FileName = "mstsc.exe";
            _process.StartInfo.Arguments = args;
            _process.StartInfo.UseShellExecute = true;
            _process.EnableRaisingEvents = true;
            _process.Exited += (s, e) =>
            {
                Dispatcher.Invoke(() => ProcessExited?.Invoke());
            };

            _process.Start();
            
            // Wait for window handle to be created
            while (_process.MainWindowHandle == IntPtr.Zero && !_process.HasExited)
            {
                Thread.Sleep(100);
                _process.Refresh();
            }

            if (!_process.HasExited && _parentHandle != IntPtr.Zero)
            {
                _childHandle = _process.MainWindowHandle;

                int style = GetWindowLong(_childHandle, GWL_STYLE);
                style = style & ~WS_CAPTION & ~WS_THICKFRAME;
                style |= WS_CHILD;
                SetWindowLong(_childHandle, GWL_STYLE, style);

                SetParent(_childHandle, _parentHandle);
                MoveWindow(_childHandle, 0, 0, (int)ActualWidth, (int)ActualHeight, true);
            }
        }

        public void StopProcess()
        {
            if (_process != null && !_process.HasExited)
            {
                try { _process.Kill(); } catch { }
            }
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _parentHandle = CreateWindowEx(0, "STATIC", "", 
                WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 
                0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                
            return new HandleRef(this, _parentHandle);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            StopProcess();
            DestroyWindow(hwnd.Handle);
        }

        protected override void OnWindowPositionChanged(Rect rcBoundingBox)
        {
            base.OnWindowPositionChanged(rcBoundingBox);
            if (_childHandle != IntPtr.Zero)
            {
                MoveWindow(_childHandle, 0, 0, (int)rcBoundingBox.Width, (int)rcBoundingBox.Height, true);
            }
        }
    }
}
