using System.Windows.Media;

namespace SSHEasyApp.Terminal;

/// <summary>
/// A single cell in the terminal character grid.
/// </summary>
public struct Cell
{
    public char Character;
    public int ForegroundIndex;  // -1 = default
    public int BackgroundIndex;  // -1 = default
    public bool Bold;
    public bool Underline;
    public bool Reverse;

    public static Cell Empty => new()
    {
        Character = ' ',
        ForegroundIndex = -1,
        BackgroundIndex = -1,
        Bold = false,
        Underline = false,
        Reverse = false
    };
}

/// <summary>
/// Current text attributes applied to new characters.
/// </summary>
public class TextAttributes
{
    public int ForegroundIndex = -1;
    public int BackgroundIndex = -1;
    public bool Bold;
    public bool Underline;
    public bool Reverse;

    public void Reset()
    {
        ForegroundIndex = -1;
        BackgroundIndex = -1;
        Bold = false;
        Underline = false;
        Reverse = false;
    }

    public Cell ToCell(char c) => new()
    {
        Character = c,
        ForegroundIndex = ForegroundIndex,
        BackgroundIndex = BackgroundIndex,
        Bold = Bold,
        Underline = Underline,
        Reverse = Reverse
    };
}

/// <summary>
/// Terminal character grid with cursor management, scrolling, and alternate screen buffer.
/// Provides the 16-color + 256-color palette used by ANSI terminals.
/// </summary>
public class TerminalBuffer
{
    // ─── Standard 16-color palette (Tango-inspired) ─────────────
    public static readonly Color[] Palette =
    [
        // Normal colors 0-7
        Color.FromRgb(0x1d, 0x1f, 0x21), // 0 Black
        Color.FromRgb(0xcc, 0x66, 0x66), // 1 Red
        Color.FromRgb(0xb5, 0xbd, 0x68), // 2 Green
        Color.FromRgb(0xf0, 0xc6, 0x74), // 3 Yellow
        Color.FromRgb(0x81, 0xa2, 0xbe), // 4 Blue
        Color.FromRgb(0xb2, 0x94, 0xbb), // 5 Magenta
        Color.FromRgb(0x8a, 0xbe, 0xb7), // 6 Cyan
        Color.FromRgb(0xc5, 0xc8, 0xc6), // 7 White

        // Bright colors 8-15
        Color.FromRgb(0x96, 0x98, 0x96), // 8 Bright Black
        Color.FromRgb(0xde, 0x93, 0x5f), // 9 Bright Red
        Color.FromRgb(0xb5, 0xbd, 0x68), // 10 Bright Green
        Color.FromRgb(0xf0, 0xc6, 0x74), // 11 Bright Yellow
        Color.FromRgb(0x81, 0xa2, 0xbe), // 12 Bright Blue
        Color.FromRgb(0xb2, 0x94, 0xbb), // 13 Bright Magenta
        Color.FromRgb(0x8a, 0xbe, 0xb7), // 14 Bright Cyan
        Color.FromRgb(0xff, 0xff, 0xff), // 15 Bright White
    ];

    public static readonly Color DefaultForeground = Color.FromRgb(0xe6, 0xed, 0xf3);
    public static readonly Color DefaultBackground = Color.FromRgb(0x0d, 0x11, 0x17);

    // ─── State ─────────────────────────────────────────────────
    private Cell[,] _screen;
    private Cell[,]? _altScreen;
    private readonly List<Cell[]> _scrollback = [];
    private readonly TextAttributes _attrs = new();

    public int Rows { get; private set; }
    public int Cols { get; private set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public bool CursorVisible { get; set; } = true;
    public bool AutoWrap { get; set; } = true;
    public int MaxScrollback { get; set; } = 10000;

    private int _scrollRegionTop;
    private int _scrollRegionBottom;
    private int _savedCursorRow;
    private int _savedCursorCol;
    private bool _inAltScreen;
    private bool _wrapPending; // flag for deferred wrap at right margin

    public int ScrollbackCount => _scrollback.Count;
    public TextAttributes CurrentAttributes => _attrs;
    public bool InAlternateScreen => _inAltScreen;

    public TerminalBuffer(int rows, int cols)
    {
        Rows = rows;
        Cols = cols;
        _screen = CreateGrid(rows, cols);
        _scrollRegionTop = 0;
        _scrollRegionBottom = rows - 1;
    }

    private static Cell[,] CreateGrid(int rows, int cols)
    {
        var grid = new Cell[rows, cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                grid[r, c] = Cell.Empty;
        return grid;
    }

    // ─── Cell access ────────────────────────────────────────────

    public Cell GetCell(int row, int col)
    {
        if (row >= 0 && row < Rows && col >= 0 && col < Cols)
            return _screen[row, col];
        return Cell.Empty;
    }

    public Cell[] GetScrollbackLine(int index)
    {
        if (index >= 0 && index < _scrollback.Count)
            return _scrollback[index];
        return [];
    }

    // ─── Character writing ──────────────────────────────────────

    public void WriteChar(char c)
    {
        if (_wrapPending)
        {
            // Deferred wrap: now actually wrap
            CursorCol = 0;
            if (CursorRow == _scrollRegionBottom)
                ScrollUp();
            else if (CursorRow < Rows - 1)
                CursorRow++;
            _wrapPending = false;
        }

        if (CursorRow >= 0 && CursorRow < Rows && CursorCol >= 0 && CursorCol < Cols)
        {
            _screen[CursorRow, CursorCol] = _attrs.ToCell(c);
        }

        CursorCol++;
        if (CursorCol >= Cols)
        {
            if (AutoWrap)
            {
                // Defer wrap — don't immediately move to next line
                CursorCol = Cols - 1;
                _wrapPending = true;
            }
            else
            {
                CursorCol = Cols - 1;
            }
        }
    }

    // ─── Cursor movement ────────────────────────────────────────

    public void CarriageReturn() { CursorCol = 0; _wrapPending = false; }

    public void LineFeed()
    {
        _wrapPending = false;
        CursorCol = 0; // Implicit CR
        if (CursorRow == _scrollRegionBottom)
            ScrollUp();
        else if (CursorRow < Rows - 1)
            CursorRow++;
    }

    public void ReverseLineFeed()
    {
        if (CursorRow == _scrollRegionTop)
            ScrollDown();
        else if (CursorRow > 0)
            CursorRow--;
    }

    public void Tab()
    {
        _wrapPending = false;
        int nextTab = ((CursorCol / 8) + 1) * 8;
        CursorCol = Math.Min(nextTab, Cols - 1);
    }

    public void Backspace()
    {
        _wrapPending = false;
        if (CursorCol > 0) CursorCol--;
    }

    public void MoveCursor(int row, int col)
    {
        _wrapPending = false;
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorCol = Math.Clamp(col, 0, Cols - 1);
    }

    public void MoveCursorUp(int n) => MoveCursor(CursorRow - n, CursorCol);
    public void MoveCursorDown(int n) => MoveCursor(CursorRow + n, CursorCol);
    public void MoveCursorForward(int n) => MoveCursor(CursorRow, CursorCol + n);
    public void MoveCursorBackward(int n) => MoveCursor(CursorRow, CursorCol - n);

    public void SaveCursor() { _savedCursorRow = CursorRow; _savedCursorCol = CursorCol; }
    public void RestoreCursor() { MoveCursor(_savedCursorRow, _savedCursorCol); }

    // ─── Erasing ────────────────────────────────────────────────

    public void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0: // Erase below (cursor to end)
                EraseLine(0); // erase from cursor to end of line
                for (int r = CursorRow + 1; r < Rows; r++)
                    ClearRow(r);
                break;
            case 1: // Erase above (start to cursor)
                for (int r = 0; r < CursorRow; r++)
                    ClearRow(r);
                for (int c = 0; c <= CursorCol && c < Cols; c++)
                    _screen[CursorRow, c] = Cell.Empty;
                break;
            case 2: // Erase entire display
            case 3: // Erase display + scrollback
                for (int r = 0; r < Rows; r++)
                    ClearRow(r);
                if (mode == 3) _scrollback.Clear();
                break;
        }
    }

    public void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0: // Erase to end of line
                for (int c = CursorCol; c < Cols; c++)
                    _screen[CursorRow, c] = Cell.Empty;
                break;
            case 1: // Erase to start of line
                for (int c = 0; c <= CursorCol && c < Cols; c++)
                    _screen[CursorRow, c] = Cell.Empty;
                break;
            case 2: // Erase entire line
                ClearRow(CursorRow);
                break;
        }
    }

    public void EraseCharacters(int n)
    {
        for (int i = 0; i < n && CursorCol + i < Cols; i++)
            _screen[CursorRow, CursorCol + i] = Cell.Empty;
    }

    public void DeleteCharacters(int n)
    {
        for (int c = CursorCol; c < Cols; c++)
        {
            int src = c + n;
            _screen[CursorRow, c] = src < Cols ? _screen[CursorRow, src] : Cell.Empty;
        }
    }

    public void InsertCharacters(int n)
    {
        for (int c = Cols - 1; c >= CursorCol + n; c--)
            _screen[CursorRow, c] = _screen[CursorRow, c - n];
        for (int c = CursorCol; c < CursorCol + n && c < Cols; c++)
            _screen[CursorRow, c] = Cell.Empty;
    }

    private void ClearRow(int row)
    {
        for (int c = 0; c < Cols; c++)
            _screen[row, c] = Cell.Empty;
    }

    // ─── Scrolling ──────────────────────────────────────────────

    public void SetScrollRegion(int top, int bottom)
    {
        _scrollRegionTop = Math.Clamp(top, 0, Rows - 1);
        _scrollRegionBottom = Math.Clamp(bottom, 0, Rows - 1);
        if (_scrollRegionTop > _scrollRegionBottom)
            (_scrollRegionTop, _scrollRegionBottom) = (_scrollRegionBottom, _scrollRegionTop);
    }

    public void ResetScrollRegion()
    {
        _scrollRegionTop = 0;
        _scrollRegionBottom = Rows - 1;
    }

    public void ScrollUp(int n = 1)
    {
        for (int i = 0; i < n; i++)
        {
            // Save top line to scrollback (only if scroll region is full screen)
            if (_scrollRegionTop == 0 && !_inAltScreen)
            {
                var line = new Cell[Cols];
                for (int c = 0; c < Cols; c++)
                    line[c] = _screen[_scrollRegionTop, c];
                _scrollback.Add(line);
                if (_scrollback.Count > MaxScrollback)
                    _scrollback.RemoveAt(0);
            }

            // Shift rows up within scroll region
            for (int r = _scrollRegionTop; r < _scrollRegionBottom; r++)
                for (int c = 0; c < Cols; c++)
                    _screen[r, c] = _screen[r + 1, c];

            ClearRow(_scrollRegionBottom);
        }
    }

    public void ScrollDown(int n = 1)
    {
        for (int i = 0; i < n; i++)
        {
            for (int r = _scrollRegionBottom; r > _scrollRegionTop; r--)
                for (int c = 0; c < Cols; c++)
                    _screen[r, c] = _screen[r - 1, c];

            ClearRow(_scrollRegionTop);
        }
    }

    public void InsertLines(int n)
    {
        for (int i = 0; i < n; i++)
        {
            for (int r = _scrollRegionBottom; r > CursorRow; r--)
                for (int c = 0; c < Cols; c++)
                    _screen[r, c] = _screen[r - 1, c];
            ClearRow(CursorRow);
        }
    }

    public void DeleteLines(int n)
    {
        for (int i = 0; i < n; i++)
        {
            for (int r = CursorRow; r < _scrollRegionBottom; r++)
                for (int c = 0; c < Cols; c++)
                    _screen[r, c] = _screen[r + 1, c];
            ClearRow(_scrollRegionBottom);
        }
    }

    // ─── Alternate screen ───────────────────────────────────────

    public void SwitchToAlternateScreen()
    {
        if (_inAltScreen) return;
        _inAltScreen = true;
        SaveCursor();
        _altScreen = _screen;
        _screen = CreateGrid(Rows, Cols);
        ResetScrollRegion();
    }

    public void SwitchToMainScreen()
    {
        if (!_inAltScreen) return;
        _inAltScreen = false;
        if (_altScreen != null)
            _screen = _altScreen;
        _altScreen = null;
        RestoreCursor();
        ResetScrollRegion();
    }

    // ─── Resize ─────────────────────────────────────────────────

    public void Resize(int newRows, int newCols)
    {
        var newScreen = CreateGrid(newRows, newCols);

        int copyRows = Math.Min(Rows, newRows);
        int copyCols = Math.Min(Cols, newCols);

        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                newScreen[r, c] = _screen[r, c];

        _screen = newScreen;
        Rows = newRows;
        Cols = newCols;
        CursorRow = Math.Min(CursorRow, newRows - 1);
        CursorCol = Math.Min(CursorCol, newCols - 1);
        _scrollRegionTop = 0;
        _scrollRegionBottom = newRows - 1;
    }

    // ─── Color helpers ──────────────────────────────────────────

    /// <summary>
    /// Resolves a color index to a WPF Color. Index -1 returns the default color.
    /// Indices 0-15 use the standard palette.
    /// Indices 16-255 use the 256-color xterm palette.
    /// Indices 256+ are encoded as 0x01RRGGBB for true-color (24-bit).
    /// </summary>
    public static Color ResolveColor(int index, bool isBackground)
    {
        if (index == -1)
            return isBackground ? DefaultBackground : DefaultForeground;

        if (index >= 0 && index < 16)
            return Palette[index];

        if (index >= 16 && index <= 255)
            return Get256Color(index);

        // True color: encoded as 0x01RRGGBB
        if (index > 255)
        {
            byte r = (byte)((index >> 16) & 0xFF);
            byte g = (byte)((index >> 8) & 0xFF);
            byte b = (byte)(index & 0xFF);
            return Color.FromRgb(r, g, b);
        }

        return isBackground ? DefaultBackground : DefaultForeground;
    }

    private static Color Get256Color(int index)
    {
        if (index < 16)
            return Palette[index];

        if (index >= 16 && index <= 231)
        {
            // 6x6x6 color cube
            int n = index - 16;
            int b = n % 6;
            int g = (n / 6) % 6;
            int r = n / 36;
            return Color.FromRgb(
                (byte)(r == 0 ? 0 : 55 + r * 40),
                (byte)(g == 0 ? 0 : 55 + g * 40),
                (byte)(b == 0 ? 0 : 55 + b * 40));
        }

        // Grayscale 232-255
        int gray = 8 + (index - 232) * 10;
        return Color.FromRgb((byte)gray, (byte)gray, (byte)gray);
    }
}
