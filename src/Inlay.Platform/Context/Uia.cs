using System.Runtime.InteropServices;
using Inlay.Platform.Native;

namespace Inlay.Platform.Context;

/// <summary>
/// Minimal UI Automation and MSAA client through raw COM vtables (AOT-safe, no interop assemblies).
/// Slot numbers come from UIAutomationClient.h / oleacc.h in the Windows SDK (IUnknown occupies 0–2).
/// Every pointer returned here is owned by the caller and must be passed to <see cref="Release"/>.
/// </summary>
internal static unsafe class Uia
{
    private static readonly Guid ClsidCUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");
    private static readonly Guid IidIUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
    private static readonly Guid IidIUIAutomation2 = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");
    private static readonly Guid IidTextPattern2 = new("506a921a-fcc9-409f-b23b-37eb74106872");
    private static readonly Guid IidTextPattern = new("32eba289-3583-42c9-9c59-3b6d9a1e9b6a");
    private static readonly Guid IidIAccessible = new("618736e0-3c3d-11cf-810c-00aa00389b71");

    private const int UIA_TextPatternId = 10014;
    private const int UIA_TextPattern2Id = 10024;
    private const int TextUnit_Character = 0;
    private const int TextPatternRangeEndpoint_Start = 0;

    // IUIAutomation
    private const int SlotElementFromHandle = 6;
    private const int SlotGetFocusedElement = 8;

    // IUIAutomation2 (extends IUIAutomation's 58 slots)
    private const int SlotPutAutoSetFocus = 59;
    private const int SlotPutConnectionTimeout = 61;
    private const int SlotPutTransactionTimeout = 63;

    // IUIAutomationElement
    private const int SlotGetCurrentPatternAs = 14;
    private const int SlotCurrentProcessId = 20;
    private const int SlotCurrentIsPassword = 35;
    private const int SlotCurrentBoundingRectangle = 43;

    // IUIAutomationTextPattern / TextPattern2
    private const int SlotGetSelection = 5;
    private const int SlotGetCaretRange = 10;

    // IUIAutomationTextRangeArray
    private const int SlotRangeArrayLength = 3;
    private const int SlotRangeArrayGetElement = 4;

    // IUIAutomationTextRange
    private const int SlotRangeClone = 3;
    private const int SlotGetBoundingRectangles = 10;
    private const int SlotMoveEndpointByUnit = 14;

    // IAccessible
    private const int SlotAccLocation = 22;

    private static nint* Vtbl(nint obj) => *(nint**)obj;

    public static void Release(nint obj)
    {
        if (obj != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(obj)[2])(obj);
    }

    /// <summary>Creates the UIA client with short timeouts so an unresponsive app can't stall the worker.</summary>
    public static nint Create()
    {
        if (Ole32.CoCreateInstance(ClsidCUIAutomation8, 0, Ole32.CLSCTX_INPROC_SERVER, IidIUIAutomation, out var uia) < 0)
            return 0;

        nint uia2 = 0;
        var iid = IidIUIAutomation2;
        if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl(uia)[0])(uia, &iid, &uia2) >= 0 && uia2 != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, int, int>)Vtbl(uia2)[SlotPutAutoSetFocus])(uia2, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)Vtbl(uia2)[SlotPutConnectionTimeout])(uia2, 500);
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)Vtbl(uia2)[SlotPutTransactionTimeout])(uia2, 500);
            Release(uia2);
        }

        return uia;
    }

    public static nint FocusedElement(nint uia)
    {
        nint element = 0;
        return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(uia)[SlotGetFocusedElement])(uia, &element) >= 0 ? element : 0;
    }

    public static nint ElementFromHandle(nint uia, nint hwnd)
    {
        nint element = 0;
        return ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vtbl(uia)[SlotElementFromHandle])(uia, hwnd, &element) >= 0 ? element : 0;
    }

    public static bool? IsPassword(nint element)
    {
        int value = 0;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vtbl(element)[SlotCurrentIsPassword])(element, &value) >= 0 ? value != 0 : null;
    }

    public static uint ProcessId(nint element)
    {
        int pid = 0;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vtbl(element)[SlotCurrentProcessId])(element, &pid) >= 0 ? (uint)pid : 0;
    }

    public static RECT? BoundingRectangle(nint element)
    {
        RECT r;
        return ((delegate* unmanaged[Stdcall]<nint, RECT*, int>)Vtbl(element)[SlotCurrentBoundingRectangle])(element, &r) >= 0 && !r.IsEmpty ? r : null;
    }

    /// <summary>Caret rectangle from TextPattern2.GetCaretRange, falling back to the TextPattern selection.</summary>
    public static RECT? CaretRect(nint element)
    {
        var range = CaretRange(element);
        if (range == 0)
            return null;
        try
        {
            if (RangeRect(range, out var rect))
                return rect;

            // A collapsed range often reports no rectangle: measure the character before the caret
            // (the ':' just typed) and use its right edge.
            nint clone = 0;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(range)[SlotRangeClone])(range, &clone) < 0 || clone == 0)
                return null;
            try
            {
                int moved = 0;
                ((delegate* unmanaged[Stdcall]<nint, int, int, int, int*, int>)Vtbl(clone)[SlotMoveEndpointByUnit])(clone, TextPatternRangeEndpoint_Start, TextUnit_Character, -1, &moved);
                if (moved != 0 && RangeRect(clone, out var previous))
                    return new RECT { Left = previous.Right, Top = previous.Top, Right = previous.Right + 1, Bottom = previous.Bottom };
            }
            finally
            {
                Release(clone);
            }

            return null;
        }
        finally
        {
            Release(range);
        }
    }

    private static nint CaretRange(nint element)
    {
        var iid2 = IidTextPattern2;
        nint pattern = 0;
        if (((delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)Vtbl(element)[SlotGetCurrentPatternAs])(element, UIA_TextPattern2Id, &iid2, &pattern) >= 0 && pattern != 0)
        {
            try
            {
                int active = 0;
                nint range = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int*, nint*, int>)Vtbl(pattern)[SlotGetCaretRange])(pattern, &active, &range) >= 0 && range != 0)
                    return range;
            }
            finally
            {
                Release(pattern);
            }
        }

        var iid = IidTextPattern;
        if (((delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)Vtbl(element)[SlotGetCurrentPatternAs])(element, UIA_TextPatternId, &iid, &pattern) < 0 || pattern == 0)
            return 0;
        try
        {
            nint array = 0;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(pattern)[SlotGetSelection])(pattern, &array) < 0 || array == 0)
                return 0;
            try
            {
                int length = 0;
                nint range = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Vtbl(array)[SlotRangeArrayLength])(array, &length) >= 0 && length > 0
                    && ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vtbl(array)[SlotRangeArrayGetElement])(array, length - 1, &range) >= 0)
                    return range;
                return 0;
            }
            finally
            {
                Release(array);
            }
        }
        finally
        {
            Release(pattern);
        }
    }

    /// <summary>Union of the last line's rectangle of a text range (SAFEARRAY of left, top, width, height doubles).</summary>
    private static bool RangeRect(nint range, out RECT rect)
    {
        rect = default;
        nint psa = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(range)[SlotGetBoundingRectangles])(range, &psa) < 0 || psa == 0)
            return false;
        try
        {
            if (OleAut32.SafeArrayGetLBound(psa, 1, out var lower) < 0 || OleAut32.SafeArrayGetUBound(psa, 1, out var upper) < 0)
                return false;
            var count = upper - lower + 1;
            if (count < 4)
                return false;
            double* data;
            if (OleAut32.SafeArrayAccessData(psa, (void**)&data) < 0)
                return false;
            try
            {
                var last = ((count / 4) - 1) * 4; // the caret is on the last rectangle's line
                rect = new RECT
                {
                    Left = (int)data[last],
                    Top = (int)data[last + 1],
                    Right = (int)(data[last] + Math.Max(1, data[last + 2])),
                    Bottom = (int)(data[last + 1] + data[last + 3]),
                };
                return rect.Height > 0;
            }
            finally
            {
                _ = OleAut32.SafeArrayUnaccessData(psa);
            }
        }
        finally
        {
            _ = OleAut32.SafeArrayDestroy(psa);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VARIANT
    {
        public ushort vt;
        public ushort r1;
        public ushort r2;
        public ushort r3;
        public nint value;
        public nint value2;
    }

    /// <summary>MSAA: the system caret object of a window (works for many Win32 and Office surfaces).</summary>
    public static RECT? MsaaCaret(nint hwnd)
    {
        if (OleAcc.AccessibleObjectFromWindow(hwnd, User32.OBJID_CARET, IidIAccessible, out var acc) < 0 || acc == 0)
            return null;
        try
        {
            int x = 0, y = 0, w = 0, h = 0;
            var self = new VARIANT { vt = 3 }; // VT_I4, CHILDID_SELF
            // VARIANT is larger than 8 bytes, so the x64 ABI passes it by reference.
            var hr = ((delegate* unmanaged[Stdcall]<nint, int*, int*, int*, int*, VARIANT*, int>)Vtbl(acc)[SlotAccLocation])(acc, &x, &y, &w, &h, &self);
            if (hr < 0 || (x == 0 && y == 0 && h == 0))
                return null;
            return new RECT { Left = x, Top = y, Right = x + Math.Max(w, 1), Bottom = y + h };
        }
        finally
        {
            Release(acc);
        }
    }
}
