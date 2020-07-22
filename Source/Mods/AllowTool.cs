#define MPCOMPAT_ALLOWTOOL_CONTEXT_MENU
#define MPCOMPAT_ALLOWTOOL_SELECT_SIMILAR
#define MPCOMPAT_ALLOWTOOL_STRIP_MINE

using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Verse;
using HarmonyLib;
using Multiplayer.API;

namespace Multiplayer.Compat
{
    /// <summary>Allow Tool by UnlimitedHugs</summary>
    /// <see href="https://github.com/UnlimitedHugs/RimworldAllowTool"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=761421485"/>
    [MpCompatFor("UnlimitedHugs.AllowTool")]
    class AllowTool
    {
        internal static bool enabled = false;

#if MPCOMPAT_ALLOWTOOL_CONTEXT_MENU
        static MethodInfo hugslibShiftGetter;
        static MethodInfo hugslibAltGetter;
        static MethodInfo hugslibCtrlGetter;
#endif

        public AllowTool(ModContentPack mod)
        {
            enabled = true;

#if MPCOMPAT_ALLOWTOOL_CONTEXT_MENU
            // For syncing keyboard state (for menu actions)
            Type hugsUtilityType = AccessTools.TypeByName("HugsLib.Utils.HugsLibUtility");
            hugslibShiftGetter = AccessTools.DeclaredPropertyGetter(hugsUtilityType, "ShiftIsHeld");
            hugslibAltGetter = AccessTools.DeclaredPropertyGetter(hugsUtilityType, "AltIsHeld");
            hugslibCtrlGetter = AccessTools.DeclaredPropertyGetter(hugsUtilityType, "ControlIsHeld");

            // Sync context menus
            {
                // Patch all calls to the original ActivateAndHandleResult() to our proxy method
                HarmonyMethod transpiler = new HarmonyMethod(typeof(AllowTool), nameof(TranspilerForActivateAndHandleResult));
                foreach (var methodName in new[]
                {
                    "AllowTool.Context.BaseContextMenuEntry+<>c__DisplayClass21_0:<MakeStandardOption>b__1",
                    "AllowTool.Context.ContextMenuProvider:TryInvokeHotkeyAction"
                })
                {
                    MpCompat.harmony.Patch(AccessTools.Method(methodName), transpiler: transpiler);
                }

                // Stop AllowTool from showing message if the action isn't actually executed by the current client
                MpCompat.harmony.Patch(AccessTools.Method("AllowTool.Context.ActivationResult:ShowMessage"), prefix: new HarmonyMethod(typeof(AllowTool), nameof(PrefixActivationShowMessage)));

                // Patch the keyboard inputs - they can alter menus' behaviour, causing desync
                HarmonyMethod postfix = new HarmonyMethod(typeof(AllowTool), nameof(PostfixHugslibKeyInput));
                foreach (var method in new[] { hugslibShiftGetter, hugslibAltGetter, hugslibCtrlGetter })
                {
                    MarkNoInlining(method);
                    MpCompat.harmony.Patch(method, postfix: postfix);
                }

                MP.RegisterSyncWorker<MenuActivateAction>(SyncWorkerForActivateAction, typeof(MenuActivateAction), false);
                MP.RegisterSyncWorker<MenuActivateContext>(SyncWorkerForActivateContext, typeof(MenuActivateContext), false);
                MP.RegisterSyncWorker<ContextMenuEntryId>(SyncWorkerForContextMenuEntryId, typeof(ContextMenuEntryId), false);
                MP.RegisterSyncMethod(AccessTools.DeclaredMethod(typeof(AllowTool), nameof(NotifyMenuActivate))).SetContext(SyncContext.CurrentMap | SyncContext.MapSelected);
            }
#endif

#if MPCOMPAT_ALLOWTOOL_SELECT_SIMILAR
            // Sync 'Select similar' designator
            {
                Type designatorType = AccessTools.TypeByName("AllowTool.Designator_SelectSimilar");
                HarmonyMethod prefix = new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AllowTool), nameof(PrefixDesignateCell)), before: new[] { "multiplayer" }, priority: Priority.First);
                HarmonyMethod postfix = new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AllowTool), nameof(PostfixDesignateCell)));
                foreach (var type in designatorType.AllSubclasses().Concat(designatorType))
                {
                    foreach (var methodName in new[] { nameof(Designator.DesignateSingleCell), nameof(Designator.DesignateMultiCell), nameof(Designator.DesignateThing) })
                    {
                        MethodInfo method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                        if (method == null) continue;

                        MpCompat.harmony.Patch(method, prefix: prefix, postfix: postfix);
                    }
                }
            }
#endif
        }

#if MPCOMPAT_ALLOWTOOL_CONTEXT_MENU
        #region Sync context menus

        static Dictionary<object, ContextMenuEntryId> menuEntryLookup;
        static Dictionary<Type, Array> menuEntryReverseLookup;

        static void PopulateMenuLookupTableIfNeeded()
        {
            if (menuEntryLookup == null || menuEntryReverseLookup == null)
            {
                Array menuProviders = (Array)AccessTools.DeclaredField(AccessTools.TypeByName("AllowTool.Context.DesignatorContextMenuController"), "menuProviders").GetValue(null);

                Type contextMenuProviderType = AccessTools.TypeByName("AllowTool.Context.ContextMenuProvider");
                PropertyInfo designatorTypeProperty = AccessTools.DeclaredProperty(contextMenuProviderType, "HandledDesignatorType");
                FieldInfo entriesField = AccessTools.DeclaredField(contextMenuProviderType, "entries");

                menuEntryLookup = new Dictionary<object, ContextMenuEntryId>();
                menuEntryReverseLookup = new Dictionary<Type, Array>();
                foreach (object menuProvider in menuProviders)
                {
                    Type handledType = (Type)designatorTypeProperty.GetValue(menuProvider);
                    Array entries = (Array)entriesField.GetValue(menuProvider);
                    for (int i = 0; i < entries.Length; i++)
                        menuEntryLookup.Add(entries.GetValue(i), new ContextMenuEntryId(handledType, i));
                    menuEntryReverseLookup.Add(handledType, entries);
                }
            }
        }

        // [HarmonyPatch("HugsLib.Utils.HugsLibUtility:get_***IsHeld")]
        static void PostfixHugslibKeyInput(MethodBase __originalMethod, ref bool __result)
        {
            if (currentContext is MenuActivateContext ctx)
            {
                // Override keyboard state
                switch (__originalMethod.Name)
                {
                    case "get_ShiftIsHeld":
                        __result = ctx.shiftIsHeld;
                        break;
                    case "get_AltIsHeld":
                        __result = ctx.altIsHeld;
                        break;
                    case "get_ControlIsHeld":
                        __result = ctx.ctrlIsHeld;
                        break;
                    default:
                        throw new InvalidOperationException("What the hell?");
                }
            }
        }

        static MethodInfo originalActivateMethod;
        static volatile bool supressActivationMessage = false;

        // Redirects all calls to ActivateAndHandleResult() to our proxy method
        // [HarmonyPatch("AllowTool.Context.BaseContextMenuEntry.<>c__DisplayClass21_0:<MakeStandardOption>b__1")]
        // [HarmonyPatch("AllowTool.Context.ContextMenuProvider.TryInvokeHotkeyAction")]
        static IEnumerable<CodeInstruction> TranspilerForActivateAndHandleResult(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            originalActivateMethod = AccessTools.DeclaredMethod(AccessTools.TypeByName("AllowTool.Context.BaseContextMenuEntry"), "ActivateAndHandleResult");
            var proxyActivateMethod = AccessTools.DeclaredMethod(typeof(AllowTool), nameof(ProxyActivateAndHandleResult));

            bool found = false;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(originalActivateMethod))
                {
                    found = true;
                    yield return new CodeInstruction(OpCodes.Ldc_I4_0);  // isRemote = false
                    yield return new CodeInstruction(OpCodes.Call, proxyActivateMethod);
                }
                else
                {
                    yield return instruction;
                }
            }

            if (!found) Log.Error("MPCompat :: [AllowTool] Unable to patch the call to ActivateAndHandleResult() in " + original.ToString());
        }

        // [HarmonyPatch("AllowTool.Context.ActivationResult:ShowMessage")]
        static bool PrefixActivationShowMessage()
        {
            // Stops the method from being executed
            return !supressActivationMessage;
        }

        static volatile bool localNotify = false;   // Is the notify method called by the current client (instead of the remote sync handler)?
        static MenuActivateContext? currentContext; // Keyboard state when the action is activated

        // HarmonyTranspilerTarget
        static void ProxyActivateAndHandleResult(object __instance, Designator designator, bool isRemote)
        {
            string entryName = __instance.GetType().Name;
            bool shouldSync = !(
                isRemote ||                                         // This method is remotely called by the sync handler, don't sync again
                entryName == "MenuEntry_MineSelectStripMine" ||     // Isn't designating anything, just selecting a designator
                entryName == "MenuEntry_SelectSimilarAll" ||        // Isn't really a designator, just a fancy selector
                entryName == "MenuEntry_SelectSimilarVisible");

            if (shouldSync)
            {
                localNotify = true;

                // Let's boardcast the action to other clients
                NotifyMenuActivate(new MenuActivateAction
                {
                    isRemote = false,
                    menuEntry = __instance,
                    designator = designator,
                }, new MenuActivateContext
                {
                    shiftIsHeld = (bool)hugslibShiftGetter.Invoke(null, null),
                    altIsHeld = (bool)hugslibAltGetter.Invoke(null, null),
                    ctrlIsHeld = (bool)hugslibCtrlGetter.Invoke(null, null)
                });

                if (localNotify)
                {
                    localNotify = false;
                    return;   // The sync method has been cancelled by the sync handler - we need to cancel this method too, otherwise we'll desync
                }
            }

            if (isRemote)
            {
                // Stop AllowTool from showing message if the action isn't actually executed by the current client
                supressActivationMessage = true;
            }

            try
            {
                originalActivateMethod.Invoke(__instance, new[] { designator });
            } finally
            {
                supressActivationMessage = false;
            }
        }

        // [SyncMethod].SetContext(SyncContext.CurrentMap | SyncContext.MapSelected);
        static void NotifyMenuActivate(MenuActivateAction action, MenuActivateContext ctx)
        {
            // The sync method hasn't been cancelled - it's a local call
            localNotify = false;
            if (action.isRemote)
            {
                currentContext = ctx;
                ProxyActivateAndHandleResult(action.menuEntry, action.designator, true);
                currentContext = null;
            }
        }

        // [SyncWorker]
        static void SyncWorkerForActivateAction(SyncWorker sync, ref MenuActivateAction action)
        {
            PopulateMenuLookupTableIfNeeded();

            if (sync.isWriting)
            {
                if (!menuEntryLookup.TryGetValue(action.menuEntry, out ContextMenuEntryId menuEntryId))
                    throw new InvalidOperationException("Unknown menu entry " + action.menuEntry.ToString());
                sync.Write(menuEntryId);
                sync.Write(action.designator.GetType());
            }
            else
            {
                action = new MenuActivateAction
                {
                    isRemote = true   // Always true when reading
                };

                ContextMenuEntryId menuId = sync.Read<ContextMenuEntryId>();
                if (!menuEntryReverseLookup.TryGetValue(menuId.type, out Array menuProviderEntries))
                {
                    throw new InvalidOperationException("Unknown menu designator type " + menuId.type.ToString());
                }
                if (menuId.index < 0 || menuId.index >= menuProviderEntries.Length)
                {
                    throw new ArgumentOutOfRangeException("Invalid menu entry index for " + menuId.type.ToString() + ": " + menuId.index);
                }
                action.menuEntry = menuProviderEntries.GetValue(menuId.index);
                action.designator = (Designator)Activator.CreateInstance(sync.Read<Type>());
            }
        }

        // [SyncWorker]
        static void SyncWorkerForActivateContext(SyncWorker sync, ref MenuActivateContext ctx)
        {
            sync.Bind(ref ctx.shiftIsHeld);
            sync.Bind(ref ctx.altIsHeld);
            sync.Bind(ref ctx.ctrlIsHeld);
        }

        // [SyncWorker]
        static void SyncWorkerForContextMenuEntryId(SyncWorker sync, ref ContextMenuEntryId id)
        {
            if (sync.isWriting)
            {
                sync.Write(id.type);
                sync.Write(id.index);
            } else
            {
                id = new ContextMenuEntryId(sync.Read<Type>(), sync.Read<int>());
            }
        }

        readonly struct ContextMenuEntryId
        {
            public ContextMenuEntryId(Type type, int index)
            {
                this.type = type;
                this.index = index;
            }

            public readonly Type type;
            public readonly int index;
        }

        internal struct MenuActivateAction
        {
            public bool isRemote;
            public object menuEntry;
            public Designator designator;
        }

        internal struct MenuActivateContext
        {
            public bool shiftIsHeld;
            public bool altIsHeld;
            public bool ctrlIsHeld;
        }

        #endregion
#endif

#if MPCOMPAT_ALLOWTOOL_SELECT_SIMILAR
        #region Sync 'Select Similar' designator

        static MethodInfo pickFromReverseDesignatorMethod;
        static FieldInfo dontSyncField;

        // Stop MP from syncing certain designator
        // [HarmonyPatch]
        // [HarmonyBefore({ "multiplayer" })]
        // [HarmonyPriority(Priority.First)]
        static void PrefixDesignateCell(Designator __instance, out bool __state)
        {
            if (dontSyncField == null)
                dontSyncField = AccessTools.DeclaredField(AccessTools.TypeByName("Multiplayer.Client.Multiplayer"), "dontSync");

            Type designatorType = __instance.GetType();
            if (designatorType.Namespace == "AllowTool")
                switch (designatorType.Name)
                {
                    case "Designator_SelectSimilarReverse":
                        if (pickFromReverseDesignatorMethod == null)
                            pickFromReverseDesignatorMethod = AccessTools.Method("AllowTool.Context.DesignatorContextMenuController:TryPickDesignatorFromReverseDesignator");

                        pickFromReverseDesignatorMethod.Invoke(null, new[] { __instance });
                        goto case "Designator_SelectSimilar"; // Fall through

                    case "Designator_SelectSimilar":
                    case "Designator_StripMine":              // (this is a empty method, no need to sync)
                        dontSyncField.SetValue(null, true);
                        __state = true;                       // Mark for restoration
                        return;
                }
            __state = false;
        }

        // [HarmonyPatch]
        static void PostfixDesignateCell(bool __state)
        {
            if (__state)
            {
                dontSyncField.SetValue(null, false);
            }
        }

        #endregion
#endif

#if MPCOMPAT_ALLOWTOOL_SELECT_SIMILAR
        #region Sync strip mine designator

        // Patches to AllowTool's designators can only be applied after all defs are loaded
        [StaticConstructorOnStartup]
        internal static class StripMineDesignatorPatch
        {
            readonly static MethodInfo syncPrefixMethod;

            static StripMineDesignatorPatch()
            {
                if (!enabled) return;

                syncPrefixMethod = AccessTools.Method("Multiplayer.Client.DesignatorPatches:DesignateMultiCell");
                MpCompat.harmony.Patch(AccessTools.Method("AllowTool.Designator_StripMine:DesignateCells"), prefix:
                    new HarmonyMethod(typeof(StripMineDesignatorPatch), nameof(PrefixDesignateMultiCell)));
            }

            // [HarmonyPatch("AllowTool.Designator_StripMine:DesignateCells")]
            static bool PrefixDesignateMultiCell(IEnumerable<IntVec3> __0)
            {
                // Using the standard designating sync, no need to write our own
                return (bool)syncPrefixMethod.Invoke(null, new object[] { new RimWorld.Designator_Mine(), __0 });
            }
        }

        #endregion
#endif

        internal static void MarkNoInlining(MethodBase method)
        {
            MethodInfo m = AccessTools.Method("Multiplayer.Client.MpUtil:MarkNoInlining");
            m.Invoke(null, new[] { method });
        }
    }
}
