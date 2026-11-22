using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Windows that only go along with the pointer: a tool's icon and text beside the pointer (GenUI.DrawMouseAttachment,
	/// the text one 200 x 9999 units below and right of it) and tooltips (ActiveTip). With a mouse they are always just
	/// off the pointer, so they never get a click. On a touch screen the pointer stays where the last finger was, and
	/// the next touch below or right of it landed in such a window: the engine gives a press to the topmost window that
	/// holds it, and the tap was lost (with a tool selected, the Architect menu's categories on that side and the tool
	/// buttons below did nothing on the first tap). Here they are left out when the engine looks for the window under
	/// the pointer, and when the touch layer does.
	/// </summary>
	internal static class MouseDecorations
	{
		private static IList lastWindows;
		private static FieldInfo idField;
		private static PropertyInfo idProperty;
		private static readonly List<(int index, object item)> removed = new List<(int, object)>();
		private static readonly HashSet<int> ids = new HashSet<int>();

		public static void Start(Harmony harmony)
		{
			try
			{
				FieldInfo field = AccessTools.Field(typeof(GUI), "_lastWindows");
				lastWindows = field?.GetValue(null) as IList;
				Type pending = field?.FieldType.GetGenericArguments()[0];
				idField = pending == null ? null : AccessTools.Field(pending, "Id");
				idProperty = idField == null && pending != null ? AccessTools.Property(pending, "Id") : null;
				if (lastWindows == null || (idField == null && idProperty == null))
				{
					Console.WriteLine("[mobile] mouse decorations: the engine's window list is not as expected; left out");
					return;
				}
				harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.DispatchInputToWindows)), prefix: new HarmonyMethod(typeof(MouseDecorations), nameof(BeforeDispatch)), finalizer: new HarmonyMethod(typeof(MouseDecorations), nameof(AfterDispatch)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mouse decorations not installed: " + e.Message);
				lastWindows = null;
			}
		}

		private static int IdOf(object pendingWindow)
		{
			return (int)(idField != null ? idField.GetValue(pendingWindow) : idProperty.GetValue(pendingWindow));
		}

		/// <summary>The ids (as the engine knows the windows) of the windows that go along with the pointer now.</summary>
		private static HashSet<int> Current()
		{
			ids.Clear();
			WindowStack stack = Find.UIRoot?.windows;
			if (stack == null)
			{
				return ids;
			}
			IList<Window> windows = stack.Windows;
			for (int i = 0; i < windows.Count; i++)
			{
				if (windows[i] is ImmediateWindow immediate && FollowsPointer(immediate.doWindowFunc?.Method.DeclaringType))
				{
					ids.Add(immediate.ID);
				}
			}
			return ids;
		}

		private static bool FollowsPointer(Type type)
		{
			for (; type != null; type = type.DeclaringType)
			{
				if (type == typeof(GenUI) || type == typeof(ActiveTip))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>Prefix of GUI.DispatchInputToWindows: for an event that goes by the pointer's position, those windows are taken out of the engine's list for the moment.</summary>
		public static void BeforeDispatch()
		{
			removed.Clear();
			Event current = Event.current;
			if (lastWindows == null || current == null || current.rawType == EventType.KeyDown || current.rawType == EventType.KeyUp || lastWindows.Count == 0)
			{
				return;
			}
			HashSet<int> following = Current();
			if (following.Count == 0)
			{
				return;
			}
			for (int i = lastWindows.Count - 1; i >= 0; i--)
			{
				object item = lastWindows[i];
				if (following.Contains(IdOf(item)))
				{
					removed.Add((i, item));
					lastWindows.RemoveAt(i);
				}
			}
		}

		/// <summary>Finalizer: they go back where they were.</summary>
		public static Exception AfterDispatch(Exception __exception)
		{
			for (int k = removed.Count - 1; k >= 0; k--)
			{
				lastWindows.Insert(removed[k].index, removed[k].item);
			}
			removed.Clear();
			return __exception;
		}

		/// <summary>For the touch layer, after the windows were drawn: which of this frame's windows (in drawing order) go along with the pointer.</summary>
		public static void Mark(bool[] marks, int count)
		{
			if (lastWindows == null || lastWindows.Count != count)
			{
				Array.Clear(marks, 0, Math.Min(marks.Length, count));
				return;
			}
			HashSet<int> following = Current();
			for (int i = 0; i < count; i++)
			{
				marks[i] = following.Count > 0 && following.Contains(IdOf(lastWindows[i]));
			}
		}
	}
}
