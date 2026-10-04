using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace CloCloWidget;

// Clipboard.SetText throws whenever another program happens to hold the
// clipboard open (clipboard managers, remote-desktop sync…), and every
// copy in this app runs straight from a click handler — unguarded, that
// exception takes the whole widget down. Retries briefly, then gives up
// quietly and reports it.
public static class SafeClipboard
{
    public static bool TrySetText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (Exception e) when (e is COMException or ExternalException)
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }
}
