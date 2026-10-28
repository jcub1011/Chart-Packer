using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using ChartShared.Core.Launching;

namespace ChartBuilder.Services
{
    /// <summary>The taskbar's Jump List, through the shell's <c>ICustomDestinationList</c>.</summary>
    /// <remarks>
    /// <para>
    /// The list is a custom category of shell links, one per song. Each runs Chart Builder with the song's path, is titled
    /// with the song's name, and has the path as its description, which is the tooltip the shell shows.
    /// </para>
    /// <para>
    /// Windows keeps the list for the process's AppUserModelID, which Chart Builder leaves to Windows: it comes from the
    /// exe's path, so the taskbar button shows the list pinned or not, and a button pinned by an earlier version too. The
    /// interfaces are declared here, in vtable order, with only the methods used given real signatures: the app is trimmed
    /// and compiled ahead of time, where <c>[ComImport]</c> isn't an option.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    internal static partial class WindowsJumpList
    {
        private const ushort VtLpwstr = 31;
        private const uint CoinitApartmentThreaded = 0x2;
        private const uint CoinitDisableOle1Dde = 0x4;
        private const uint ClsctxAll = 0x17;
        private const int ArgumentsCapacity = 4096;

        private static readonly Guid DestinationListClsid = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
        private static readonly Guid DestinationListIid = new("6332DEBF-87B5-4670-90C0-5E57B408A49E");
        private static readonly Guid CollectionClsid = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
        private static readonly Guid CollectionIid = new("5632B1A4-E38A-400A-928A-D4CD63230295");
        private static readonly Guid ObjectArrayIid = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
        private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
        private static readonly Guid ShellLinkIid = new("000214F9-0000-0000-C000-000000000046");
        private static readonly Guid PropertyStoreIid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

        // System.Title, which is what the list shows for a link.
        private static readonly PropertyKey TitleKey = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B86D"), 2);

        private static readonly StrategyBasedComWrappers Wrappers = new();

        /// <summary>Empties the list's category, as the uninstaller does so that no entries outlive the app.</summary>
        /// <remarks>Throws <see cref="COMException"/> when the shell won't take the list. Call it on a single-threaded apartment thread.</remarks>
        public static void Clear() => Publish("", "", _ => []);

        /// <summary>Replaces the list's <paramref name="category"/> with the entries <paramref name="entriesFor"/> returns.</summary>
        /// <param name="category">The category's title.</param>
        /// <param name="exePath">What each entry runs.</param>
        /// <param name="entriesFor">The entries, given the arguments of those the user removed from the list, which Windows refuses to list again.</param>
        /// <remarks>Throws <see cref="COMException"/> when the shell won't take the list. Call it on a single-threaded apartment thread.</remarks>
        public static void Publish(string category, string exePath, Func<IReadOnlyList<string>, IReadOnlyList<JumpListEntry>> entriesFor)
        {
            int init = CoInitializeEx(0, CoinitApartmentThreaded | CoinitDisableOle1Dde);
            var scope = new ComScope();
            ICustomDestinationList? list = null;
            try
            {
                list = scope.Create<ICustomDestinationList>(DestinationListClsid, DestinationListIid, out _);
                list.BeginList(out _, ObjectArrayIid, out nint removedPointer);
                scope.Own(removedPointer);

                var entries = entriesFor(RemovedArguments(scope, removedPointer));
                if (entries.Count > 0)
                {
                    var collection = scope.Create<IObjectCollection>(CollectionClsid, CollectionIid, out nint collectionPointer);
                    foreach (var entry in entries)
                        collection.AddObject(CreateLink(scope, exePath, entry));

                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(collectionPointer, in ObjectArrayIid, out nint arrayPointer));
                    scope.Own(arrayPointer);
                    list.AppendCategory(category, arrayPointer);
                }

                // With nothing to list this clears the category the last list made.
                list.CommitList();
                list = null;
            }
            finally
            {
                // A list that wasn't committed is dropped, leaving the last one as it was.
                try
                {
                    list?.AbortList();
                }
                catch (COMException)
                {
                }

                scope.Dispose();
                if (init >= 0)
                    CoUninitialize();
            }
        }

        // The arguments of the links the user removed, which are what tells one of ours from another.
        private static List<string> RemovedArguments(ComScope scope, nint removedPointer)
        {
            List<string> arguments = [];
            if (removedPointer == 0)
                return arguments;

            var removed = scope.Wrap<IObjectArray>(removedPointer);
            removed.GetCount(out uint count);
            nint buffer = Marshal.AllocHGlobal(ArgumentsCapacity * sizeof(char));
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    try
                    {
                        removed.GetAt(i, ShellLinkIid, out nint linkPointer);
                        scope.Own(linkPointer);
                        scope.Wrap<IShellLinkW>(linkPointer).GetArguments(buffer, ArgumentsCapacity);
                        if (Marshal.PtrToStringUni(buffer) is { Length: > 0 } text)
                            arguments.Add(text);
                    }
                    catch (COMException)
                    {
                        // Not a link, so not one of ours.
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return arguments;
        }

        private static nint CreateLink(ComScope scope, string exePath, JumpListEntry entry)
        {
            var link = scope.Create<IShellLinkW>(ShellLinkClsid, ShellLinkIid, out nint linkPointer);
            link.SetPath(exePath);
            link.SetArguments(entry.Arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? "");
            link.SetDescription(entry.Path);
            link.SetIconLocation(exePath, 0);

            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(linkPointer, in PropertyStoreIid, out nint storePointer));
            scope.Own(storePointer);
            var properties = scope.Wrap<IPropertyStore>(storePointer);
            var title = StringVariant(entry.Title);
            try
            {
                var key = TitleKey;
                properties.SetValue(ref key, ref title);
                properties.Commit();
            }
            finally
            {
                ClearVariant(ref title);
            }

            return linkPointer;
        }

        /// <summary>A <c>VT_LPWSTR</c> <c>PROPVARIANT</c> holding <paramref name="value"/>, made as propvarutil.h's inline <c>InitPropVariantFromString</c> does: propsys.dll doesn't export it.</summary>
        /// <remarks>Its string is in COM task memory, so <see cref="ClearVariant"/> frees it.</remarks>
        internal static PropVariant StringVariant(string value) => new() { Type = VtLpwstr, Value1 = Marshal.StringToCoTaskMemUni(value) };

        /// <summary>Frees what <paramref name="value"/> holds and empties it.</summary>
        internal static void ClearVariant(ref PropVariant value) => _ = PropVariantClear(ref value);

        /// <summary>The COM objects of one update, let go of together.</summary>
        private sealed class ComScope : IDisposable
        {
            private readonly List<nint> _pointers = [];
            private readonly List<object> _objects = [];

            /// <summary>Makes the object <paramref name="clsid"/> names, as <typeparamref name="T"/>; <paramref name="pointer"/> is its interface pointer.</summary>
            public T Create<T>(Guid clsid, Guid iid, out nint pointer) where T : class
            {
                Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, 0, ClsctxAll, iid, out pointer));
                Own(pointer);
                return Wrap<T>(pointer);
            }

            /// <summary>Releases <paramref name="pointer"/> with the scope.</summary>
            public void Own(nint pointer)
            {
                if (pointer != 0)
                    _pointers.Add(pointer);
            }

            /// <summary>The object <paramref name="pointer"/> is an interface of, as <typeparamref name="T"/>.</summary>
            public T Wrap<T>(nint pointer) where T : class
            {
                var wrapped = (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
                _objects.Add(wrapped);
                return wrapped;
            }

            public void Dispose()
            {
                foreach (var item in _objects)
                {
                    if (item is ComObject com)
                        com.FinalRelease();
                }

                foreach (nint pointer in _pointers)
                    Marshal.Release(pointer);
                _objects.Clear();
                _pointers.Clear();
            }
        }

        [LibraryImport("ole32.dll")]
        private static partial int CoInitializeEx(nint reserved, uint coInit);

        [LibraryImport("ole32.dll")]
        private static partial void CoUninitialize();

        [LibraryImport("ole32.dll")]
        private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

        [LibraryImport("ole32.dll")]
        private static partial int PropVariantClear(ref PropVariant value);
    }

    /// <summary>A shell property's identity.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    /// <summary>A <c>PROPVARIANT</c>, as the shell fills and clears one: only its size and its alignment matter here.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Value1;
        public nint Value2;
    }

    /// <summary>The shell's <c>ICustomDestinationList</c>, in vtable order.</summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    internal partial interface ICustomDestinationList
    {
        // Unused: the list belongs to the process's own ID. It stays for the slot it takes in the vtable.
        void SetAppID(string appId);
        void BeginList(out uint minSlots, in Guid iid, out nint removed);
        void AppendCategory(string category, nint items);
        void AppendKnownCategory(int category);
        void AddUserTasks(nint items);
        void CommitList();
        void GetRemovedDestinations(in Guid iid, out nint removed);
        void DeleteList(string appId);
        void AbortList();
    }

    /// <summary>The shell's <c>IObjectArray</c>.</summary>
    [GeneratedComInterface]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    internal partial interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, in Guid iid, out nint item);
    }

    /// <summary>The shell's <c>IObjectCollection</c>, which extends <c>IObjectArray</c>: its methods come first in the vtable.</summary>
    [GeneratedComInterface]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    internal partial interface IObjectCollection : IObjectArray
    {
        void AddObject(nint item);
        void AddFromArray(nint items);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    /// <summary>The shell's <c>IShellLinkW</c>, in vtable order.</summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(nint file, int capacity, nint data, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription(nint description, int capacity);
        void SetDescription(string description);
        void GetWorkingDirectory(nint directory, int capacity);
        void SetWorkingDirectory(string directory);
        void GetArguments(nint arguments, int capacity);
        void SetArguments(string arguments);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(nint path, int capacity, out int index);
        void SetIconLocation(string path, int index);
        void SetRelativePath(string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath(string file);
    }

    /// <summary>The shell's <c>IPropertyStore</c>, in vtable order.</summary>
    [GeneratedComInterface]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
