using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SSHEasyApp.Terminal;

/// <summary>
/// Custom WPF control that renders a terminal character grid with ANSI color support.
/// Handles keyboard input and provides a scrollbar for scrollback history.
/// </summary>
public class TerminalControl : FrameworkElement
{
    // ─── Configuration ──────────────────────────────────────────
    private const double FontSize = 14.0;
    private static readonly Typeface TerminalTypeface = new(
        new FontFamily("Cascadia Mono, Consolas, Courier New"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TerminalTypefaceBold = new(
        new FontFamily("Cascadia Mono, Consolas, Courier New"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    // ─── Internal state ─────────────────────────────────────────
    private TerminalBuffer _buffer = null!;
    private AnsiParser _parser = null!;
    private double _charWidth;
    private double _charHeight;
    private double _dpiScale = 1.0;
    private int _scrollOffset; // 0 = bottom (live), positive = scrolled up
    private bool _cursorBlinkState = true;
    private readonly DispatcherTimer _cursorTimer;
    private readonly Dictionary<Color, SolidColorBrush> _brushCache = [];

    /// <summary>
    /// Fired when the user types something. The byte array should be sent to the SSH stream.
    /// </summary>
    public event Action<byte[]>? UserInput;

    /// <summary>
    /// Fired when the terminal control resizes (new cols, new rows).
    /// </summary>
    public event Action<int, int>? TerminalResized;

    public TerminalBuffer Buffer => _buffer;
    public AnsiParser Parser => _parser;

    public TerminalControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;

        _cursorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(530)
        };
        _cursorTimer.Tick += (_, _) =>
        {
            _cursorBlinkState = !_cursorBlinkState;
            InvalidateVisual();
        };

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Measure character dimensions
        var ps = PresentationSource.FromVisual(this);
        _dpiScale = ps?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        MeasureCharSize();

        // Initialize buffer with calculated size
        int cols = Math.Max(1, (int)(ActualWidth / _charWidth));
        int rows = Math.Max(1, (int)(ActualHeight / _charHeight));

        _buffer = new TerminalBuffer(rows, cols);
        _parser = new AnsiParser(_buffer);
        _parser.ResponseRequired += data => UserInput?.Invoke(data);

        Focus();
        _cursorTimer.Start();
    }

    private void MeasureCharSize()
    {
        var ft = new FormattedText(
            "M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            TerminalTypeface, FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        _charWidth = ft.WidthIncludingTrailingWhitespace;
        _charHeight = ft.Height;

        // Ensure monospace width (use a wider char too)
        var ft2 = new FormattedText(
            "W", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            TerminalTypeface, FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        _charWidth = Math.Max(_charWidth, ft2.WidthIncludingTrailingWhitespace);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_buffer == null || _charWidth <= 0 || _charHeight <= 0) return;

        int newCols = Math.Max(1, (int)(ActualWidth / _charWidth));
        int newRows = Math.Max(1, (int)(ActualHeight / _charHeight));

        if (newCols != _buffer.Cols || newRows != _buffer.Rows)
        {
            _buffer.Resize(newRows, newCols);
            _scrollOffset = 0;
            TerminalResized?.Invoke(newCols, newRows);
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Process incoming data from the SSH stream. Call from the UI thread.
    /// </summary>
    public void ProcessData(byte[] data)
    {
        if (_parser == null) return;
        _parser.Process(data);

        // Auto-scroll to bottom when new data arrives
        _scrollOffset = 0;

        InvalidateVisual();
    }

    // ─── Rendering ──────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        if (_buffer == null) return;

        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Background
        dc.DrawRectangle(GetBrush(TerminalBuffer.DefaultBackground), null,
            new Rect(0, 0, ActualWidth, ActualHeight));

        int totalScrollback = _buffer.ScrollbackCount;
        int visibleRows = _buffer.Rows;

        for (int visualRow = 0; visualRow < visibleRows; visualRow++)
        {
            double y = visualRow * _charHeight;

            // Determine which line to display
            int scrollbackLine = totalScrollback - _scrollOffset + visualRow - visibleRows;
            bool isScrollback = scrollbackLine >= 0 && scrollbackLine < totalScrollback && _scrollOffset > 0;

            Cell[] lineCells;

            if (isScrollback)
            {
                lineCells = _buffer.GetScrollbackLine(scrollbackLine);
            }
            else
            {
                int bufferRow = visualRow - Math.Max(0, _scrollOffset - (totalScrollback - visibleRows + visualRow));
                if (_scrollOffset > 0)
                {
                    int row = visualRow + (totalScrollback - _scrollOffset) - totalScrollback;
                    if (row < 0)
                    {
                        // This visual row maps to scrollback
                        int sbIndex = totalScrollback + row;
                        if (sbIndex >= 0 && sbIndex < totalScrollback)
                        {
                            lineCells = _buffer.GetScrollbackLine(sbIndex);
                            RenderLine(dc, lineCells, y, ppd, -1, -1);
                            continue;
                        }
                        continue;
                    }
                    else
                    {
                        lineCells = new Cell[_buffer.Cols];
                        for (int c = 0; c < _buffer.Cols; c++)
                            lineCells[c] = _buffer.GetCell(row, c);
                        int cursorRow = _scrollOffset == 0 ? _buffer.CursorRow : -1;
                        int cursorCol = _scrollOffset == 0 ? _buffer.CursorCol : -1;
                        RenderLine(dc, lineCells, y, ppd,
                            row == cursorRow ? cursorCol : -1, -1);
                        continue;
                    }
                }

                // Normal (non-scrolled) view
                lineCells = new Cell[_buffer.Cols];
                for (int c = 0; c < _buffer.Cols; c++)
                    lineCells[c] = _buffer.GetCell(visualRow, c);

                bool isCursorRow = visualRow == _buffer.CursorRow && _scrollOffset == 0;
                RenderLine(dc, lineCells, y, ppd,
                    isCursorRow ? _buffer.CursorCol : -1, -1);
                continue;
            }

            RenderLine(dc, lineCells, y, ppd, -1, -1);
        }

        // Scrollbar indicator (subtle)
        if (_scrollOffset > 0 && totalScrollback > 0)
        {
            double sbHeight = ActualHeight;
            double thumbHeight = Math.Max(20, sbHeight * visibleRows / (totalScrollback + visibleRows));
            double thumbTop = sbHeight * (1.0 - (double)(_scrollOffset + visibleRows) / (totalScrollback + visibleRows));
            thumbTop = Math.Max(0, thumbTop);

            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                null,
                new Rect(ActualWidth - 6, thumbTop, 4, thumbHeight),
                2, 2);
        }
    }

    private void RenderLine(DrawingContext dc, Cell[] cells, double y, double ppd,
        int cursorCol, int selectionStart)
    {
        if (cells.Length == 0) return;

        // Draw background rectangles for non-default backgrounds
        for (int c = 0; c < cells.Length; c++)
        {
            var cell = cells[c];
            int bgIdx = cell.Reverse ? cell.ForegroundIndex : cell.BackgroundIndex;
            if (bgIdx == -1 && !cell.Reverse) continue;

            Color bgColor;
            if (cell.Reverse && cell.ForegroundIndex == -1)
                bgColor = TerminalBuffer.DefaultForeground;
            else if (cell.Reverse)
                bgColor = TerminalBuffer.ResolveColor(cell.ForegroundIndex, false);
            else
                bgColor = TerminalBuffer.ResolveColor(bgIdx, true);

            if (bgColor != TerminalBuffer.DefaultBackground)
            {
                dc.DrawRectangle(GetBrush(bgColor), null,
                    new Rect(c * _charWidth, y, _charWidth, _charHeight));
            }
        }

        // Build text string from cells
        var chars = new char[cells.Length];
        for (int c = 0; c < cells.Length; c++)
            chars[c] = cells[c].Character == '\0' ? ' ' : cells[c].Character;

        var text = new string(chars);
        var ft = new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            TerminalTypeface, FontSize, GetBrush(TerminalBuffer.DefaultForeground), ppd);

        // Apply per-character formatting
        int runStart = 0;
        for (int c = 0; c < cells.Length; c++)
        {
            var cell = cells[c];

            // Detect color run boundaries
            bool isEnd = c == cells.Length - 1;
            bool colorChanges = !isEnd && (
                cells[c + 1].ForegroundIndex != cell.ForegroundIndex ||
                cells[c + 1].Bold != cell.Bold ||
                cells[c + 1].Reverse != cell.Reverse);

            if (isEnd || colorChanges)
            {
                int count = c - runStart + 1;

                // Foreground color
                int fgIdx = cell.Reverse ? cell.BackgroundIndex : cell.ForegroundIndex;
                Color fgColor;
                if (cell.Reverse && cell.BackgroundIndex == -1)
                    fgColor = TerminalBuffer.DefaultBackground;
                else if (cell.Reverse)
                    fgColor = TerminalBuffer.ResolveColor(cell.BackgroundIndex, true);
                else
                    fgColor = TerminalBuffer.ResolveColor(fgIdx, false);

                // Bold shifts normal colors to bright
                if (cell.Bold && !cell.Reverse && cell.ForegroundIndex >= 0 && cell.ForegroundIndex < 8)
                    fgColor = TerminalBuffer.ResolveColor(cell.ForegroundIndex + 8, false);

                if (fgColor != TerminalBuffer.DefaultForeground)
                    ft.SetForegroundBrush(GetBrush(fgColor), runStart, count);

                if (cell.Bold)
                    ft.SetFontWeight(FontWeights.Bold, runStart, count);

                if (cell.Underline)
                    ft.SetTextDecorations(TextDecorations.Underline, runStart, count);

                runStart = c + 1;
            }
        }

        dc.DrawText(ft, new Point(0, y));

        // Draw cursor
        if (cursorCol >= 0 && cursorCol < cells.Length && _buffer.CursorVisible && _cursorBlinkState)
        {
            double cx = cursorCol * _charWidth;
            dc.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(180, 88, 166, 255)),
                null,
                new Rect(cx, y, _charWidth, _charHeight));

            // Redraw the character under the cursor in dark color
            var cursorChar = cells[cursorCol].Character;
            if (cursorChar != ' ' && cursorChar != '\0')
            {
                var cursorText = new FormattedText(
                    cursorChar.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    TerminalTypeface, FontSize, GetBrush(TerminalBuffer.DefaultBackground), ppd);
                dc.DrawText(cursorText, new Point(cx, y));
            }
        }
    }

    private SolidColorBrush GetBrush(Color color)
    {
        if (!_brushCache.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            brush.Freeze();
            _brushCache[color] = brush;
        }
        return brush;
    }

    // ─── Keyboard Input ─────────────────────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        byte[]? data = null;
        bool appMode = _parser?.ApplicationCursorKeys ?? false;

        switch (e.Key)
        {
            case Key.Enter:
                data = [0x0D]; // CR
                break;
            case Key.Back:
                data = [0x7F]; // DEL (standard for modern terminals)
                break;
            case Key.Tab:
                data = [0x09];
                break;
            case Key.Escape:
                data = [0x1B];
                break;
            case Key.Up:
                data = appMode ? "\x1bOA"u8.ToArray() : "\x1b[A"u8.ToArray();
                break;
            case Key.Down:
                data = appMode ? "\x1bOB"u8.ToArray() : "\x1b[B"u8.ToArray();
                break;
            case Key.Right:
                data = appMode ? "\x1bOC"u8.ToArray() : "\x1b[C"u8.ToArray();
                break;
            case Key.Left:
                data = appMode ? "\x1bOD"u8.ToArray() : "\x1b[D"u8.ToArray();
                break;
            case Key.Home:
                data = "\x1b[H"u8.ToArray();
                break;
            case Key.End:
                data = "\x1b[F"u8.ToArray();
                break;
            case Key.PageUp:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    _scrollOffset = Math.Min(_scrollOffset + _buffer.Rows, _buffer.ScrollbackCount);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
                data = "\x1b[5~"u8.ToArray();
                break;
            case Key.PageDown:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    _scrollOffset = Math.Max(_scrollOffset - _buffer.Rows, 0);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
                data = "\x1b[6~"u8.ToArray();
                break;
            case Key.Insert:
                data = "\x1b[2~"u8.ToArray();
                break;
            case Key.Delete:
                data = "\x1b[3~"u8.ToArray();
                break;
            case Key.F1: data = "\x1bOP"u8.ToArray(); break;
            case Key.F2: data = "\x1bOQ"u8.ToArray(); break;
            case Key.F3: data = "\x1bOR"u8.ToArray(); break;
            case Key.F4: data = "\x1bOS"u8.ToArray(); break;
            case Key.F5: data = "\x1b[15~"u8.ToArray(); break;
            case Key.F6: data = "\x1b[17~"u8.ToArray(); break;
            case Key.F7: data = "\x1b[18~"u8.ToArray(); break;
            case Key.F8: data = "\x1b[19~"u8.ToArray(); break;
            case Key.F9: data = "\x1b[20~"u8.ToArray(); break;
            case Key.F10: data = "\x1b[21~"u8.ToArray(); break;
            case Key.F11: data = "\x1b[23~"u8.ToArray(); break;
            case Key.F12: data = "\x1b[24~"u8.ToArray(); break;

            default:
                // Handle Ctrl+key combinations
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    // Ctrl+V = paste
                    if (e.Key == Key.V)
                    {
                        if (Clipboard.ContainsText())
                        {
                            var text = Clipboard.GetText();
                            data = System.Text.Encoding.UTF8.GetBytes(text);
                        }
                        e.Handled = true;
                        if (data != null) UserInput?.Invoke(data);
                        return;
                    }

                    // Ctrl+C, Ctrl+D, Ctrl+Z, etc.
                    int keyVal = KeyInterop.VirtualKeyFromKey(e.Key);
                    if (keyVal >= 0x41 && keyVal <= 0x5A) // A-Z
                    {
                        data = [(byte)(keyVal - 0x40)]; // Ctrl+A = 0x01, Ctrl+C = 0x03, etc.
                    }
                }
                break;
        }

        if (data != null)
        {
            _scrollOffset = 0; // Auto-scroll to bottom on input
            UserInput?.Invoke(data);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            var data = System.Text.Encoding.UTF8.GetBytes(e.Text);
            _scrollOffset = 0;
            UserInput?.Invoke(data);
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int delta = e.Delta > 0 ? 3 : -3;
        _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, _buffer?.ScrollbackCount ?? 0);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
    }

    // ─── Layout ─────────────────────────────────────────────────

    public (int cols, int rows) GetTerminalSize()
    {
        if (_charWidth <= 0 || _charHeight <= 0)
            return (80, 24);
        return (
            Math.Max(1, (int)(ActualWidth / _charWidth)),
            Math.Max(1, (int)(ActualHeight / _charHeight))
        );
    }
}
