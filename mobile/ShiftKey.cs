using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The modifier keys for a finger, switched on and off by the key wheel's Shift, Ctrl and Alt (KeyWheel; Shift was a
	/// button of its own at the left edge until 2026-10-20, Ctrl and Alt came with the whole keyboard on the wheel). While
	/// one is on the engine sees its left key held down (a real key event through the Android GLFW, so the game, and any
	/// mod that asks for the key, sees what it sees on a PC): every tap on the screen and every key of the wheel comes
	/// with it, as a click or a key does with the key held on a PC. Shift, for instance:
	///   a tap on a colonist, an animal, a thing or a portrait of the colonist bar adds it to the selection, or
	///   takes it out again; a tap on the ground leaves the selection as it is;
	///   an order (a hold on the map, or a choice from the menu it opens) is queued behind what the colonists are
	///   doing instead of replacing it;
	///   whatever else the game does with Shift (ten at a time in the trade and transfer dialogs).
	/// They go off by themselves when the game is left.
	/// </summary>
	internal sealed class LatchedKey
	{
		[DllImport("glfw")] private static extern void glfwAndroidInjectKey(int key, int action, int mods);

		private const int Press = 1, Release = 0;

		// GLFW's left modifier keys and their bits in GLFW's mods.
		public static readonly LatchedKey Ctrl = new LatchedKey("Ctrl", 341, KeyCode.LeftControl, 2);
		public static readonly LatchedKey Shift = new LatchedKey("Shift", 340, KeyCode.LeftShift, 1);
		public static readonly LatchedKey Alt = new LatchedKey("Alt", 342, KeyCode.LeftAlt, 4);
		public static readonly LatchedKey[] All = { Ctrl, Shift, Alt };

		public readonly string Name;
		public readonly int Glfw, Mod;
		private readonly KeyCode unity;
		private bool on;
		private int injectedFrame = -1;

		private LatchedKey(string name, int glfw, KeyCode unity, int mod)
		{
			Name = name;
			Glfw = glfw;
			this.unity = unity;
			Mod = mod;
		}

		public bool On => on;

		/// <summary>GLFW's mods of the keys that are on.</summary>
		public static int Mods
		{
			get
			{
				int mods = 0;
				foreach (LatchedKey key in All)
				{
					if (key.on)
					{
						mods |= key.Mod;
					}
				}
				return mods;
			}
		}

		public void Set(bool value)
		{
			on = value;
			glfwAndroidInjectKey(Glfw, value ? Press : Release, 0);
			injectedFrame = Time.frameCount;
			Console.WriteLine("[mobile] " + Name + " " + (value ? "held" : "let go"));
		}

		/// <summary>Once a frame: off when no game is played; and held down again if the engine let go of the key (it does when the window comes back).</summary>
		public static void UpdateAll()
		{
			foreach (LatchedKey key in All)
			{
				key.Update();
			}
		}

		private void Update()
		{
			if (!on)
			{
				return;
			}
			if (Current.ProgramState != ProgramState.Playing || !KeyWheel.Enabled)
			{
				Set(false);
			}
			else if (Time.frameCount > injectedFrame + 3 && !Input.GetKey(unity))
			{
				Set(true);
			}
		}
	}
}
