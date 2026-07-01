namespace SSHEasyApp.Terminal;

/// <summary>
/// ANSI/VT100 escape sequence parser. Processes raw byte streams from the SSH shell
/// and updates the TerminalBuffer accordingly.
/// </summary>
public class AnsiParser
{
    private enum State
    {
        Normal,
        Escape,       // received ESC
        Csi,          // received ESC [
        CsiParam,     // collecting CSI parameters
        OscString,    // received ESC ]
        EscHash,      // received ESC #
        SetCharset    // received ESC ( or ESC )
    }

    private readonly TerminalBuffer _buffer;
    private State _state = State.Normal;
    private readonly List<int> _params = [];
    private string _paramString = "";
    private bool _privateMode;        // CSI ? prefix
    private string _oscString = "";
    private bool _applicationCursorKeys;

    /// <summary>
    /// Fired when the terminal needs to send a response back to the server
    /// (e.g., cursor position report).
    /// </summary>
    public event Action<byte[]>? ResponseRequired;

    /// <summary>
    /// Whether application cursor key mode is active (changes arrow key output).
    /// </summary>
    public bool ApplicationCursorKeys => _applicationCursorKeys;

    public AnsiParser(TerminalBuffer buffer)
    {
        _buffer = buffer;
    }

    /// <summary>
    /// Process incoming raw bytes from the SSH shell stream.
    /// </summary>
    public void Process(byte[] data)
    {
        foreach (byte b in data)
        {
            ProcessByte(b);
        }
    }

    private void ProcessByte(byte b)
    {
        switch (_state)
        {
            case State.Normal:
                ProcessNormal(b);
                break;
            case State.Escape:
                ProcessEscape(b);
                break;
            case State.Csi:
            case State.CsiParam:
                ProcessCsi(b);
                break;
            case State.OscString:
                ProcessOsc(b);
                break;
            case State.EscHash:
                _state = State.Normal; // ignore ESC # sequences for now
                break;
            case State.SetCharset:
                _state = State.Normal; // ignore charset selection
                break;
        }
    }

    // ─── Normal mode ────────────────────────────────────────────

    private void ProcessNormal(byte b)
    {
        switch (b)
        {
            case 0x1B: // ESC
                _state = State.Escape;
                break;
            case 0x08: // BS (backspace)
                _buffer.Backspace();
                break;
            case 0x09: // HT (tab)
                _buffer.Tab();
                break;
            case 0x0A: // LF (line feed)
            case 0x0B: // VT (vertical tab)
            case 0x0C: // FF (form feed)
                _buffer.LineFeed();
                break;
            case 0x0D: // CR (carriage return)
                _buffer.CarriageReturn();
                break;
            case 0x07: // BEL (bell) - ignore
                break;
            case 0x00: // NUL - ignore
            case 0x7F: // DEL - ignore
                break;
            default:
                if (b >= 0x20) // printable
                {
                    _buffer.WriteChar((char)b);
                }
                else if (b >= 0xC0) // UTF-8 multi-byte start
                {
                    // Basic UTF-8 handling: treat as printable
                    _buffer.WriteChar((char)b);
                }
                break;
        }
    }

    // ─── Escape mode ────────────────────────────────────────────

    private void ProcessEscape(byte b)
    {
        _state = State.Normal;

        switch (b)
        {
            case (byte)'[': // CSI
                _state = State.Csi;
                _params.Clear();
                _paramString = "";
                _privateMode = false;
                break;
            case (byte)']': // OSC
                _state = State.OscString;
                _oscString = "";
                break;
            case (byte)'(': // Set G0 charset
            case (byte)')': // Set G1 charset
                _state = State.SetCharset;
                break;
            case (byte)'#':
                _state = State.EscHash;
                break;
            case (byte)'D': // Index (line feed)
                _buffer.LineFeed();
                break;
            case (byte)'M': // Reverse Index
                _buffer.ReverseLineFeed();
                break;
            case (byte)'E': // Next Line (CR + LF)
                _buffer.CarriageReturn();
                _buffer.LineFeed();
                break;
            case (byte)'7': // Save Cursor (DECSC)
                _buffer.SaveCursor();
                break;
            case (byte)'8': // Restore Cursor (DECRC)
                _buffer.RestoreCursor();
                break;
            case (byte)'c': // Full Reset (RIS)
                _buffer.CurrentAttributes.Reset();
                _buffer.MoveCursor(0, 0);
                _buffer.EraseInDisplay(2);
                _buffer.ResetScrollRegion();
                break;
            case (byte)'=': // Application Keypad (DECKPAM) - ignore
            case (byte)'>': // Normal Keypad (DECKPNM) - ignore
                break;
            default:
                // Unknown escape - ignore
                break;
        }
    }

    // ─── CSI mode ───────────────────────────────────────────────

    private void ProcessCsi(byte b)
    {
        // Check for private mode prefix
        if (b == '?' && _paramString.Length == 0 && _params.Count == 0)
        {
            _privateMode = true;
            _state = State.CsiParam;
            return;
        }

        // Parameter characters (digits and ;)
        if (b >= '0' && b <= '9')
        {
            _paramString += (char)b;
            _state = State.CsiParam;
            return;
        }

        if (b == ';')
        {
            _params.Add(_paramString.Length > 0 ? int.Parse(_paramString) : 0);
            _paramString = "";
            _state = State.CsiParam;
            return;
        }

        // Intermediate bytes (space, !, etc.) - just absorb for now
        if (b >= 0x20 && b <= 0x2F)
        {
            _state = State.CsiParam;
            return;
        }

        // Final byte - execute
        if (_paramString.Length > 0)
            _params.Add(int.Parse(_paramString));

        _state = State.Normal;
        ExecuteCsi((char)b);
    }

    private int Param(int index, int defaultVal = 0)
    {
        if (index < _params.Count && _params[index] > 0)
            return _params[index];
        return defaultVal;
    }

    private void ExecuteCsi(char cmd)
    {
        if (_privateMode)
        {
            ExecuteDecPrivateMode(cmd);
            return;
        }

        switch (cmd)
        {
            case 'A': // Cursor Up
                _buffer.MoveCursorUp(Param(0, 1));
                break;
            case 'B': // Cursor Down
                _buffer.MoveCursorDown(Param(0, 1));
                break;
            case 'C': // Cursor Forward
                _buffer.MoveCursorForward(Param(0, 1));
                break;
            case 'D': // Cursor Backward
                _buffer.MoveCursorBackward(Param(0, 1));
                break;
            case 'E': // Cursor Next Line
                _buffer.MoveCursor(_buffer.CursorRow + Param(0, 1), 0);
                break;
            case 'F': // Cursor Previous Line
                _buffer.MoveCursor(_buffer.CursorRow - Param(0, 1), 0);
                break;
            case 'G': // Cursor Horizontal Absolute
                _buffer.MoveCursor(_buffer.CursorRow, Param(0, 1) - 1);
                break;
            case 'H': // Cursor Position
            case 'f': // Horizontal and Vertical Position
                _buffer.MoveCursor(Param(0, 1) - 1, Param(1, 1) - 1);
                break;
            case 'J': // Erase in Display
                _buffer.EraseInDisplay(Param(0, 0));
                break;
            case 'K': // Erase in Line
                _buffer.EraseLine(Param(0, 0));
                break;
            case 'L': // Insert Lines
                _buffer.InsertLines(Param(0, 1));
                break;
            case 'M': // Delete Lines
                _buffer.DeleteLines(Param(0, 1));
                break;
            case 'P': // Delete Characters
                _buffer.DeleteCharacters(Param(0, 1));
                break;
            case 'S': // Scroll Up
                _buffer.ScrollUp(Param(0, 1));
                break;
            case 'T': // Scroll Down
                _buffer.ScrollDown(Param(0, 1));
                break;
            case 'X': // Erase Characters
                _buffer.EraseCharacters(Param(0, 1));
                break;
            case '@': // Insert Characters
                _buffer.InsertCharacters(Param(0, 1));
                break;
            case 'd': // Cursor Vertical Absolute
                _buffer.MoveCursor(Param(0, 1) - 1, _buffer.CursorCol);
                break;
            case 'm': // SGR - Select Graphic Rendition
                ExecuteSgr();
                break;
            case 'n': // Device Status Report
                if (Param(0) == 6)
                {
                    // Cursor Position Report
                    var report = $"\x1b[{_buffer.CursorRow + 1};{_buffer.CursorCol + 1}R";
                    ResponseRequired?.Invoke(System.Text.Encoding.ASCII.GetBytes(report));
                }
                break;
            case 'r': // Set Scrolling Region (DECSTBM)
                if (_params.Count >= 2)
                    _buffer.SetScrollRegion(Param(0, 1) - 1, Param(1, _buffer.Rows) - 1);
                else
                    _buffer.ResetScrollRegion();
                _buffer.MoveCursor(0, 0);
                break;
            case 's': // Save Cursor Position
                _buffer.SaveCursor();
                break;
            case 'u': // Restore Cursor Position
                _buffer.RestoreCursor();
                break;
            case 't': // Window manipulation - mostly ignore
                break;
            case 'c': // Device Attributes
                // Report as VT220
                ResponseRequired?.Invoke("\x1b[?62;c"u8.ToArray());
                break;
            default:
                // Unknown CSI command - ignore
                break;
        }
    }

    // ─── DEC Private Mode ───────────────────────────────────────

    private void ExecuteDecPrivateMode(char cmd)
    {
        int mode = Param(0);

        switch (cmd)
        {
            case 'h': // Set Mode
                switch (mode)
                {
                    case 1: // Application Cursor Keys
                        _applicationCursorKeys = true;
                        break;
                    case 7: // Auto Wrap
                        _buffer.AutoWrap = true;
                        break;
                    case 12: // Cursor Blink - ignore
                        break;
                    case 25: // Show Cursor
                        _buffer.CursorVisible = true;
                        break;
                    case 47:
                    case 1047:
                        _buffer.SwitchToAlternateScreen();
                        break;
                    case 1049: // Alt screen + save cursor
                        _buffer.SaveCursor();
                        _buffer.SwitchToAlternateScreen();
                        _buffer.EraseInDisplay(2);
                        break;
                    case 2004: // Bracketed Paste Mode - note but don't act on
                        break;
                }
                break;
            case 'l': // Reset Mode
                switch (mode)
                {
                    case 1:
                        _applicationCursorKeys = false;
                        break;
                    case 7:
                        _buffer.AutoWrap = false;
                        break;
                    case 25:
                        _buffer.CursorVisible = false;
                        break;
                    case 47:
                    case 1047:
                        _buffer.SwitchToMainScreen();
                        break;
                    case 1049:
                        _buffer.SwitchToMainScreen();
                        _buffer.RestoreCursor();
                        break;
                    case 2004:
                        break;
                }
                break;
        }
    }

    // ─── SGR (Select Graphic Rendition) ─────────────────────────

    private void ExecuteSgr()
    {
        if (_params.Count == 0)
        {
            _buffer.CurrentAttributes.Reset();
            return;
        }

        var attrs = _buffer.CurrentAttributes;

        for (int i = 0; i < _params.Count; i++)
        {
            int code = _params[i];

            switch (code)
            {
                case 0: attrs.Reset(); break;
                case 1: attrs.Bold = true; break;
                case 3: break; // Italic - not rendered but accepted
                case 4: attrs.Underline = true; break;
                case 7: attrs.Reverse = true; break;
                case 22: attrs.Bold = false; break;
                case 23: break; // Not italic
                case 24: attrs.Underline = false; break;
                case 27: attrs.Reverse = false; break;

                // Foreground colors 30-37
                case >= 30 and <= 37:
                    attrs.ForegroundIndex = code - 30;
                    break;
                case 38: // Extended foreground
                    i = ParseExtendedColor(i, true);
                    break;
                case 39: // Default foreground
                    attrs.ForegroundIndex = -1;
                    break;

                // Background colors 40-47
                case >= 40 and <= 47:
                    attrs.BackgroundIndex = code - 40;
                    break;
                case 48: // Extended background
                    i = ParseExtendedColor(i, false);
                    break;
                case 49: // Default background
                    attrs.BackgroundIndex = -1;
                    break;

                // Bright foreground 90-97
                case >= 90 and <= 97:
                    attrs.ForegroundIndex = code - 90 + 8;
                    break;

                // Bright background 100-107
                case >= 100 and <= 107:
                    attrs.BackgroundIndex = code - 100 + 8;
                    break;
            }
        }
    }

    /// <summary>
    /// Parses 256-color (38;5;N) or 24-bit color (38;2;R;G;B) sequences.
    /// Returns the updated parameter index.
    /// </summary>
    private int ParseExtendedColor(int paramIndex, bool isForeground)
    {
        var attrs = _buffer.CurrentAttributes;

        if (paramIndex + 1 < _params.Count)
        {
            int type = _params[paramIndex + 1];

            if (type == 5 && paramIndex + 2 < _params.Count)
            {
                // 256-color: 38;5;N or 48;5;N
                int colorIndex = _params[paramIndex + 2];
                if (isForeground)
                    attrs.ForegroundIndex = colorIndex;
                else
                    attrs.BackgroundIndex = colorIndex;
                return paramIndex + 2;
            }
            else if (type == 2 && paramIndex + 4 < _params.Count)
            {
                // 24-bit: 38;2;R;G;B or 48;2;R;G;B
                int r = _params[paramIndex + 2];
                int g = _params[paramIndex + 3];
                int b = _params[paramIndex + 4];
                // Encode as 0x01RRGGBB (values > 255 signal true color)
                int encoded = 0x01000000 | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
                if (isForeground)
                    attrs.ForegroundIndex = encoded;
                else
                    attrs.BackgroundIndex = encoded;
                return paramIndex + 4;
            }
        }

        return paramIndex;
    }

    // ─── OSC (Operating System Command) ─────────────────────────

    private void ProcessOsc(byte b)
    {
        if (b == 0x07) // BEL - terminates OSC
        {
            HandleOsc();
            _state = State.Normal;
            return;
        }
        if (b == 0x1B) // ESC - might be ESC \ (ST)
        {
            // We'll just end OSC here for simplicity
            HandleOsc();
            _state = State.Normal;
            return;
        }
        _oscString += (char)b;
    }

    private void HandleOsc()
    {
        // OSC sequences set terminal title etc. - we could expose this
        // For now, just ignore them
    }
}
