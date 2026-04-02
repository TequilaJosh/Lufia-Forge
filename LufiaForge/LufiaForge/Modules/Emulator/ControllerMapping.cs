using System.Text.Json.Serialization;
using System.Windows.Input;

namespace LufiaForge.Modules.Emulator;

/// <summary>The 12 SNES controller buttons.</summary>
public enum SnesButton
{
    Up, Down, Left, Right,
    A, B, X, Y,
    L, R,
    Start, Select
}

/// <summary>
/// Maps each SNES button to a WPF Key.
/// Default bindings mimic the Snes9x defaults so existing muscle memory works.
/// Persisted as part of AppSettings.
/// </summary>
public class ControllerMapping
{
    /// <summary>Maps SnesButton → WPF Key (stored as the Key enum string for JSON).</summary>
    public Dictionary<SnesButton, Key> Buttons { get; set; } = DefaultBindings();

    // Display names for the UI
    public static readonly IReadOnlyDictionary<SnesButton, string> Labels =
        new Dictionary<SnesButton, string>
        {
            { SnesButton.Up,     "D-Pad Up"   }, { SnesButton.Down,  "D-Pad Down"  },
            { SnesButton.Left,   "D-Pad Left" }, { SnesButton.Right, "D-Pad Right" },
            { SnesButton.A,      "A"          }, { SnesButton.B,     "B"           },
            { SnesButton.X,      "X"          }, { SnesButton.Y,     "Y"           },
            { SnesButton.L,      "L"          }, { SnesButton.R,     "R"           },
            { SnesButton.Start,  "Start"      }, { SnesButton.Select,"Select"      },
        };

    public static Dictionary<SnesButton, Key> DefaultBindings() => new()
    {
        { SnesButton.Up,    Key.Up       },
        { SnesButton.Down,  Key.Down     },
        { SnesButton.Left,  Key.Left     },
        { SnesButton.Right, Key.Right    },
        { SnesButton.A,     Key.X        },
        { SnesButton.B,     Key.Z        },
        { SnesButton.X,     Key.S        },
        { SnesButton.Y,     Key.A        },
        { SnesButton.L,     Key.Q        },
        { SnesButton.R,     Key.W        },
        { SnesButton.Start, Key.Return   },
        { SnesButton.Select,Key.RightShift },
    };

    /// <summary>All buttons in display order.</summary>
    public static readonly SnesButton[] AllButtons =
    {
        SnesButton.Up, SnesButton.Down, SnesButton.Left, SnesButton.Right,
        SnesButton.A,  SnesButton.B,    SnesButton.X,    SnesButton.Y,
        SnesButton.L,  SnesButton.R,    SnesButton.Start,SnesButton.Select,
    };

    /// <summary>Returns the set of Keys that are currently mapped (for forwarding filter).</summary>
    public HashSet<Key> MappedKeys() => new(Buttons.Values);

    public void ResetToDefaults() => Buttons = DefaultBindings();
}
