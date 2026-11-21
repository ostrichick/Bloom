using System;
using System.Runtime.InteropServices;
using System.Threading;
using Bloom.Native;
using Bloom.WinKey;

namespace Bloom.App;

/// A Win key pressed and released on its own opens the launcher instead of the Start menu
/// Win + any other key (Win+E, Win+Shift+S, …) behaves as before; Ctrl+Esc still opens Start.
/// The hook lives on its own thread with its own message loop: if it shared the UI thread, a busy
/// frame would stall everyone's typing and Windows would silently remove the hook.
/// Blind while an elevated window has focus (UIPI): there the elevated helper takes the press when switched on
/// (ElevatedWinKey). Whichever hook takes a tap replays its release tagged, and the other one resyncs on it (WinTap).
/// How far a Win tap got (Interlocked counters only; the UI thread reports them).
/// Consumed without posted means the handoff failed; posted without fired means the hook thread
/// never ran the message; fired without requested means the UI callback never ran.
internal static class WinKeyDiag
{
    public static long Consumed, Posted, PostFailed, Fired, Requested;
    public static string Snapshot() => $"win-key: consumed={Consumed} posted={Posted} post-failed={PostFailed} fired={Fired} requested={Requested}";
}

internal sealed class WinKeyHook : IDisposable
{
    private const uint WM_APP_FIRE = Win32.WM_APP + 1;

    private readonly Action fired;
    private readonly WinTap tap = new();
    private Thread? thread;
    private uint threadId;
    private IntPtr hook;
    private Win32.HookProc? proc; // kept referenced so the GC never collects the callback

    public bool Active => hook != IntPtr.Zero;

    public WinKeyHook(Action fired) => this.fired = fired;

    public void Start()
    {
        if (thread is not null) return;
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            threadId = Win32.GetCurrentThreadId();
            proc = OnKey;
            hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, proc, Win32.GetModuleHandle(null), 0);
            ready.Set();
            if (hook == IntPtr.Zero) return;
            while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                if (msg.message == WM_APP_FIRE) Fire((byte)msg.wParam);
            Win32.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        })
        { IsBackground = true, Name = "win-key", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        ready.Wait(2000);
    }

    private IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            int m = (int)wParam.ToInt64();
            bool down = m is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
            bool up = m is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
            if (tap.Key(k.vkCode, down, up, k.dwExtraInfo == WinKeyProtocol.OwnTag, ModifiersHeld))
            {
                // swallow this release; Fire() replays it behind a mask key so Start stays closed
                Interlocked.Increment(ref WinKeyDiag.Consumed);
                if (Win32.PostThreadMessage(threadId, WM_APP_FIRE, (IntPtr)k.vkCode, IntPtr.Zero))
                {
                    Interlocked.Increment(ref WinKeyDiag.Posted);
                    return new IntPtr(1);
                }
                Interlocked.Increment(ref WinKeyDiag.PostFailed);
                // the handoff failed: passing the key through (Start opens) beats swallowing it silently
                return Win32.CallNextHookEx(hook, nCode, wParam, lParam);
            }
        }
        return Win32.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    private static bool Held(uint vk) => (Win32.GetAsyncKeyState((int)vk) & 0x8000) != 0;

    private static bool ModifiersHeld() => Held(WinTap.VK_SHIFT) || Held(WinTap.VK_CONTROL) || Held(WinTap.VK_MENU);

    private static void Key(uint vk, bool up) => Win32.keybd_event((byte)vk, 0, up ? Win32.KEYEVENTF_KEYUP : 0, WinKeyProtocol.OwnTag);

    private void Fire(byte winVk)
    {
        Interlocked.Increment(ref WinKeyDiag.Fired);
        Key(WinTap.VK_MASK, false);
        Key(WinTap.VK_MASK, true);
        Key(winVk, true);
        // our process injected the last input, so the UI thread may take the foreground
        fired();
    }

    public void Dispose()
    {
        if (threadId != 0) Win32.PostThreadMessage(threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread = null;
        threadId = 0;
    }
}
