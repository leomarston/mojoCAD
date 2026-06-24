using System;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;

namespace MojoCad.Acad.Interop
{
    /// <summary>
    /// Marshals work onto AutoCAD's main thread. The plugin captures the main-thread
    /// <see cref="SynchronizationContext"/> at load (it must call <see cref="Initialize"/> from a command
    /// or the load handler). Background callers (the agent loop) then post DB reads here so they never
    /// touch the <c>Database</c> off-thread.
    ///
    /// Reads are marshalled to the main thread (which owns the active document). Edits go through
    /// <c>ExecuteInCommandContextAsync</c> in <c>ChangeApplier</c>, which additionally provides command
    /// context and lets us take a document lock.
    /// </summary>
    public static class AcadContext
    {
        private static SynchronizationContext? _main;
        private static int _mainThreadId;

        /// <summary>Call once from AutoCAD's main thread (e.g. in IExtensionApplication.Initialize).</summary>
        public static void Initialize()
        {
            _main = SynchronizationContext.Current ?? new SynchronizationContext();
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public static bool IsOnMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>Run a function on AutoCAD's main thread and await its result.</summary>
        public static Task<T> InvokeAsync<T>(Func<T> func)
        {
            if (_main == null || IsOnMainThread)
            {
                try { return Task.FromResult(func()); }
                catch (Exception ex) { return Task.FromException<T>(ex); }
            }

            var tcs = new TaskCompletionSource<T>();
            _main.Post(_ =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, null);
            return tcs.Task;
        }

        /// <summary>Run an action on AutoCAD's main thread and await its completion.</summary>
        public static Task InvokeAsync(Action action) => InvokeAsync<object?>(() => { action(); return null; });

        /// <summary>
        /// Open a read transaction on the active document's database and project a result, on the main thread.
        /// Returns <paramref name="fallback"/> if there is no active document.
        /// </summary>
        public static Task<T> ReadAsync<T>(Func<Database, Transaction, T> reader, T fallback)
        {
            return InvokeAsync(() =>
            {
                var doc = Application.DocumentManager?.MdiActiveDocument;
                if (doc == null) return fallback;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    T result = reader(doc.Database, tr);
                    tr.Commit(); // read-only, but commit is the cheap path to close the transaction
                    return result;
                }
            });
        }
    }

    /// <summary>Helpers for AutoCAD persistent handles (the stable identity the agent uses).</summary>
    internal static class HandleUtil
    {
        /// <summary>Resolve a hex handle string to an ObjectId, or ObjectId.Null if it does not exist.</summary>
        public static ObjectId Resolve(Database db, string hexHandle)
        {
            try
            {
                long value = Convert.ToInt64(hexHandle, 16);
                var handle = new Handle(value);
                ObjectId id = db.GetObjectId(false, handle, 0);
                return id;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        public static string ToHandle(ObjectId id) => id.Handle.ToString();
    }
}
