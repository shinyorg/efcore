using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile;

/// <summary>
/// Registers the bundled <c>sqlite-vec</c> native binary as a SQLite auto-extension so the <c>vec0</c> virtual
/// table and the <c>vec_distance_*</c> functions are available on every connection opened afterwards - without
/// calling <see cref="SqliteConnection.LoadExtension(string, string?)"/>. This is the only approach that works on
/// iOS (Apple forbids <c>dlopen</c> of loose libraries) and the simplest uniform approach on Android and desktop.
/// </summary>
/// <remarks>
/// <c>UseShinyVectors()</c> calls <see cref="RegisterAutoExtension"/> for you. Call it yourself only when you open a
/// <see cref="SqliteConnection"/> before any DbContext is configured (an auto-extension only applies to connections
/// opened after it is registered).
/// </remarks>
public static partial class SqliteVec
{
    const string VecInitSymbol = "sqlite3_vec_init";

    // sqlite3_auto_extension lives in the SQLite *engine*, which differs by bundle: "e_sqlite3" for plain SQLite
    // and "e_sqlcipher" for the SQLCipher build (SQLitePCLRaw.bundle_e_sqlcipher). On iOS/tvOS whichever engine is
    // statically linked is reached via "__Internal" - the same way SQLitePCLRaw binds its own sqlite3_* calls.
    [LibraryImport("__Internal", EntryPoint = "sqlite3_auto_extension")]
    private static partial int AutoExtensionInternal(IntPtr entryPoint);

    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_auto_extension")]
    private static partial int AutoExtensionSqlite(IntPtr entryPoint);

    [LibraryImport("e_sqlcipher", EntryPoint = "sqlite3_auto_extension")]
    private static partial int AutoExtensionCipher(IntPtr entryPoint);

    // Statically-linked sqlite-vec entry point (iOS/tvOS). Declaring this P/Invoke does two jobs: it binds the
    // symbol the same way SQLitePCLRaw binds sqlite3_*, AND it anchors it against the native linker's -dead_strip
    // (-force_load pulls the .a object in, but with no reference the symbol would still be stripped). The
    // trampoline below gives us an address for auto_extension without depending on dlsym.
    [LibraryImport("__Internal", EntryPoint = "sqlite3_vec_init")]
    private static partial int VecInitInternal(IntPtr db, IntPtr pzErrMsg, IntPtr pApi);

    [UnmanagedCallersOnly]
    private static int VecInitTrampoline(IntPtr db, IntPtr pzErrMsg, IntPtr pApi)
        => VecInitInternal(db, pzErrMsg, pApi);

    static readonly object gate = new();
    static bool registered;

    // Real iOS/tvOS apps statically link BOTH the SQLite engine and sqlite-vec into the main image. Everywhere else
    // they're dynamic libraries: desktop/Android, and Mac Catalyst too (OperatingSystem.IsIOS() returns true there,
    // but it ships e_sqlite3 and sqlite-vec as dylibs, so it must be treated as dynamic).
    static bool StaticallyLinked =>
        (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()) && !OperatingSystem.IsMacCatalyst();

    /// <summary>
    /// Registers <c>sqlite-vec</c> so <c>vec0</c> is available on every connection opened afterwards. Idempotent
    /// and thread-safe.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The native binary or the SQLite engine could not be located, or the SQLite engine rejected the registration.
    /// </exception>
    public static void RegisterAutoExtension()
    {
        if (registered)
            return;

        lock (gate)
        {
            if (registered)
                return;

            // SqliteConnection's static constructor initialises whichever SQLitePCLRaw bundle the app references
            // (e_sqlite3 or e_sqlcipher). We must not touch sqlite3_* symbols before that has happened.
            RuntimeHelpers.RunClassConstructor(typeof(SqliteConnection).TypeHandle);

            int rc;
            if (StaticallyLinked)
            {
                unsafe
                {
                    delegate* unmanaged<IntPtr, IntPtr, IntPtr, int> entry = &VecInitTrampoline;
                    rc = AutoExtensionInternal((IntPtr)entry);
                }
            }
            else
            {
                rc = CallDynamicAutoExtension(ResolveDynamicVecInit());
            }

            if (rc != 0) // SQLITE_OK
                throw new InvalidOperationException(
                    $"sqlite3_auto_extension(sqlite3_vec_init) returned {rc}. The SQLite engine rejected the sqlite-vec registration.");

            registered = true;
        }
    }

    // Desktop / Android / Mac Catalyst: register against whichever engine SQLitePCLRaw loaded - "e_sqlcipher" for
    // the SQLCipher bundle, "e_sqlite3" otherwise - then fall back across the others.
    static int CallDynamicAutoExtension(IntPtr entryPoint)
    {
        var cipherFirst = GetEngineName()?.Contains("cipher", StringComparison.OrdinalIgnoreCase) == true;
        var order = cipherFirst
            ? new Func<IntPtr, int>[] { AutoExtensionCipher, AutoExtensionSqlite, AutoExtensionInternal }
            : new Func<IntPtr, int>[] { AutoExtensionSqlite, AutoExtensionCipher, AutoExtensionInternal };

        Exception? last = null;
        foreach (var call in order)
        {
            try
            {
                return call(entryPoint);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            "Could not locate sqlite3_auto_extension in the active SQLite engine (tried e_sqlite3, e_sqlcipher, and the main image).",
            last);
    }

    static string? GetEngineName()
    {
        try
        {
            return SQLitePCL.raw.GetNativeLibraryName();
        }
        catch
        {
            return null;
        }
    }

    // Loads the bundled loadable sqlite-vec binary (vec0.dylib/.so/.dll or libsqlite_vec0.so) and returns the
    // sqlite3_vec_init address. Used everywhere except statically-linked iOS/tvOS.
    static IntPtr ResolveDynamicVecInit()
    {
        foreach (var name in new[] { "sqlite_vec0", "vec0" })
        {
            if (NativeLibrary.TryLoad(name, typeof(SqliteVec).Assembly, null, out var handle) &&
                NativeLibrary.TryGetExport(handle, VecInitSymbol, out var export))
                return export;
        }

        // Last resort: it may already be in the main image.
        try
        {
            if (NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), VecInitSymbol, out var p))
                return p;
        }
        catch
        {
            // ignore - fall through to the error below
        }

        throw new InvalidOperationException(
            "Could not load the bundled sqlite-vec native binary. Ensure the Shiny.EntityFrameworkCore.Sqlite.Mobile " +
            "package shipped it for this platform and that <ShinySqliteVecNative> is not set to false.");
    }
}
