using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using static SDL3.SDL;

namespace ImGuiSDL;

internal class Application : IDisposable
{
	/// <summary>
	/// The SDL Window
	/// </summary>
	public readonly nint Window;

	/// <summary>
	/// The SDL GPU Device
	/// </summary>
	public readonly nint Device;

	/// <summary>
	/// The ImGui Renderer Implementation
	/// </summary>
	public readonly ImGuiRenderer Renderer;

	/// <summary>
	/// If the Application is Running
	/// </summary>
	public bool Running { get; private set; }

	private readonly Stopwatch timer = Stopwatch.StartNew();
	private TimeSpan time = TimeSpan.Zero;

	public Application(string name, int width, int height, bool debugMode)
	{
		// launch SDL
		if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
			throw new Exception($"{nameof(SDL_Init)} Failed: {SDL_GetError()}");

		// create graphics device
		Device = SDL_CreateGPUDevice(
			SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL | 
			SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_MSL | 
			SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV,
			debug_mode: debugMode,
			name: null!
		);
		if (Device == nint.Zero)
			throw new Exception($"{nameof(SDL_CreateGPUDevice)} Failed: {SDL_GetError()}");

		// create window
		var windowFlags = SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
		Window = SDL_CreateWindow(name, width, height, windowFlags);
		if (Window == nint.Zero)
			throw new Exception($"{nameof(SDL_CreateWindow)} Failed: {SDL_GetError()}");

		// claim SDL GPU Window
		if (!SDL_ClaimWindowForGPUDevice(Device, Window))
			throw new Exception($"{nameof(SDL_ClaimWindowForGPUDevice)} Failed: {SDL_GetError()}");

		// create imgui context
		var context = ImGui.CreateContext();
		ImGui.SetCurrentContext(context);

		// create imgui SDL_GPU renderer
		Renderer = new ImGuiRenderer(Device, Window, context);
	}

	~Application() => Dispose();

	public void Dispose()
	{
		GC.SuppressFinalize(this);
		ImGui.DestroyContext(Renderer.Context);
		Running = false;
		Renderer.Dispose();
		SDL_ReleaseWindowFromGPUDevice(Device, Window);
		SDL_DestroyWindow(Window);
		SDL_DestroyGPUDevice(Device);
		SDL_Quit();
	}

	public void Run()
	{
		Running = true;

		while (Running)
		{
			// update delta time
			ImGui.GetIO().DeltaTime = (float)(timer.Elapsed - time).TotalSeconds;
			time = timer.Elapsed;

			// run update
			PollEvents();
			Update();
			Render();
		}
	}

	private void PollEvents()
	{
		// toggle keyboard input
		if (ImGui.GetIO().WantTextInput && !SDL_TextInputActive(Window))
			SDL_StartTextInput(Window);
		else if (!ImGui.GetIO().WantTextInput && SDL_TextInputActive(Window))
			SDL_StopTextInput(Window);

		// poll SDL events
		while (SDL_PollEvent(out var ev))
		{
			ProcessImGuiEvent(ev);

			switch ((SDL_EventType)ev.type)
			{
				case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
				case SDL_EventType.SDL_EVENT_QUIT:
					Running = false;
					break;
			}
		}
	}

	private void Update()
	{
		Renderer.NewFrame();
		ImGui.NewFrame();
		if (ImGui.Begin("Hello World"))
		{
			ImGui.Text("This is ImGui x SDL x C#");
		}
		ImGui.End();
		ImGui.ShowDemoWindow();
		ImGui.EndFrame();
	}

	private void Render()
	{
		var clear = new SDL_FColor() { r = 0.1f, g = 0.05f, b = 0.08f, a = 1.0f };
		Renderer.Render(clear);
	}

	/// <summary>
	/// Allow ImGui to process an SDL Event
	/// </summary>
	private unsafe void ProcessImGuiEvent(SDL_Event ev)
	{
		var io = ImGui.GetIO();

		switch ((SDL_EventType)ev.type)
		{
			// mouse input:

			case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
				io.MousePos = new Vector2(ev.motion.x, ev.motion.y) / Renderer.Scale;
				break;

			case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
			case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
				io.AddMouseButtonEvent(GetImGuiMouseButton(ev.button.button), ev.button.down);
				break;

			case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
				io.AddMouseWheelEvent(ev.wheel.x, ev.wheel.y);
				break;

			// keyboard input:
			case SDL_EventType.SDL_EVENT_KEY_DOWN:
			case SDL_EventType.SDL_EVENT_KEY_UP:
				io.AddKeyEvent(ImGuiKey.ModCtrl, (ev.key.mod & SDL_Keymod.SDL_KMOD_CTRL) != 0);
				io.AddKeyEvent(ImGuiKey.ModShift, (ev.key.mod & SDL_Keymod.SDL_KMOD_SHIFT) != 0);
				io.AddKeyEvent(ImGuiKey.ModAlt, (ev.key.mod & SDL_Keymod.SDL_KMOD_ALT) != 0);
				io.AddKeyEvent(ImGuiKey.ModSuper, (ev.key.mod & SDL_Keymod.SDL_KMOD_GUI) != 0);
				io.AddKeyEvent(GetImGuiKey((SDL_Keycode)ev.key.key, ev.key.scancode), ev.key.down);
				break;

			case SDL_EventType.SDL_EVENT_TEXT_INPUT:
				ImGuiNative.ImGuiIO_AddInputCharactersUTF8(io.NativePtr, ev.text.text);
				break;
		}
	}

	private static int GetImGuiMouseButton(int sdlButton) => sdlButton switch
	{
		1 => 0, // left
		2 => 2, // middle
		3 => 1, // right
		_ => 0,
	};

	private static ImGuiKey GetImGuiKey(SDL_Keycode keycode, SDL_Scancode scancode)
	{
		switch (scancode)
		{
			case SDL_Scancode.SDL_SCANCODE_KP_0: return ImGuiKey.Keypad0;
			case SDL_Scancode.SDL_SCANCODE_KP_1: return ImGuiKey.Keypad1;
			case SDL_Scancode.SDL_SCANCODE_KP_2: return ImGuiKey.Keypad2;
			case SDL_Scancode.SDL_SCANCODE_KP_3: return ImGuiKey.Keypad3;
			case SDL_Scancode.SDL_SCANCODE_KP_4: return ImGuiKey.Keypad4;
			case SDL_Scancode.SDL_SCANCODE_KP_5: return ImGuiKey.Keypad5;
			case SDL_Scancode.SDL_SCANCODE_KP_6: return ImGuiKey.Keypad6;
			case SDL_Scancode.SDL_SCANCODE_KP_7: return ImGuiKey.Keypad7;
			case SDL_Scancode.SDL_SCANCODE_KP_8: return ImGuiKey.Keypad8;
			case SDL_Scancode.SDL_SCANCODE_KP_9: return ImGuiKey.Keypad9;
			case SDL_Scancode.SDL_SCANCODE_KP_PERIOD: return ImGuiKey.KeypadDecimal;
			case SDL_Scancode.SDL_SCANCODE_KP_DIVIDE: return ImGuiKey.KeypadDivide;
			case SDL_Scancode.SDL_SCANCODE_KP_MULTIPLY: return ImGuiKey.KeypadMultiply;
			case SDL_Scancode.SDL_SCANCODE_KP_MINUS: return ImGuiKey.KeypadSubtract;
			case SDL_Scancode.SDL_SCANCODE_KP_PLUS: return ImGuiKey.KeypadAdd;
			case SDL_Scancode.SDL_SCANCODE_KP_ENTER: return ImGuiKey.KeypadEnter;
			case SDL_Scancode.SDL_SCANCODE_KP_EQUALS: return ImGuiKey.KeypadEqual;
			default: break;
		}
		switch (keycode)
		{
			case SDL_Keycode.SDLK_TAB: return ImGuiKey.Tab;
			case SDL_Keycode.SDLK_LEFT: return ImGuiKey.LeftArrow;
			case SDL_Keycode.SDLK_RIGHT: return ImGuiKey.RightArrow;
			case SDL_Keycode.SDLK_UP: return ImGuiKey.UpArrow;
			case SDL_Keycode.SDLK_DOWN: return ImGuiKey.DownArrow;
			case SDL_Keycode.SDLK_PAGEUP: return ImGuiKey.PageUp;
			case SDL_Keycode.SDLK_PAGEDOWN: return ImGuiKey.PageDown;
			case SDL_Keycode.SDLK_HOME: return ImGuiKey.Home;
			case SDL_Keycode.SDLK_END: return ImGuiKey.End;
			case SDL_Keycode.SDLK_INSERT: return ImGuiKey.Insert;
			case SDL_Keycode.SDLK_DELETE: return ImGuiKey.Delete;
			case SDL_Keycode.SDLK_BACKSPACE: return ImGuiKey.Backspace;
			case SDL_Keycode.SDLK_SPACE: return ImGuiKey.Space;
			case SDL_Keycode.SDLK_RETURN: return ImGuiKey.Enter;
			case SDL_Keycode.SDLK_ESCAPE: return ImGuiKey.Escape;
			case SDL_Keycode.SDLK_APOSTROPHE: return ImGuiKey.Apostrophe;
			case SDL_Keycode.SDLK_COMMA: return ImGuiKey.Comma;
			case SDL_Keycode.SDLK_MINUS: return ImGuiKey.Minus;
			case SDL_Keycode.SDLK_PERIOD: return ImGuiKey.Period;
			case SDL_Keycode.SDLK_SLASH: return ImGuiKey.Slash;
			case SDL_Keycode.SDLK_SEMICOLON: return ImGuiKey.Semicolon;
			case SDL_Keycode.SDLK_EQUALS: return ImGuiKey.Equal;
			case SDL_Keycode.SDLK_LEFTBRACKET: return ImGuiKey.LeftBracket;
			case SDL_Keycode.SDLK_BACKSLASH: return ImGuiKey.Backslash;
			case SDL_Keycode.SDLK_RIGHTBRACKET: return ImGuiKey.RightBracket;
			case SDL_Keycode.SDLK_GRAVE: return ImGuiKey.GraveAccent;
			case SDL_Keycode.SDLK_CAPSLOCK: return ImGuiKey.CapsLock;
			case SDL_Keycode.SDLK_SCROLLLOCK: return ImGuiKey.ScrollLock;
			case SDL_Keycode.SDLK_NUMLOCKCLEAR: return ImGuiKey.NumLock;
			case SDL_Keycode.SDLK_PRINTSCREEN: return ImGuiKey.PrintScreen;
			case SDL_Keycode.SDLK_PAUSE: return ImGuiKey.Pause;
			case SDL_Keycode.SDLK_LCTRL: return ImGuiKey.LeftCtrl;
			case SDL_Keycode.SDLK_LSHIFT: return ImGuiKey.LeftShift;
			case SDL_Keycode.SDLK_LALT: return ImGuiKey.LeftAlt;
			case SDL_Keycode.SDLK_LGUI: return ImGuiKey.LeftSuper;
			case SDL_Keycode.SDLK_RCTRL: return ImGuiKey.RightCtrl;
			case SDL_Keycode.SDLK_RSHIFT: return ImGuiKey.RightShift;
			case SDL_Keycode.SDLK_RALT: return ImGuiKey.RightAlt;
			case SDL_Keycode.SDLK_RGUI: return ImGuiKey.RightSuper;
			case SDL_Keycode.SDLK_APPLICATION: return ImGuiKey.Menu;
			case SDL_Keycode.SDLK_0: return ImGuiKey._0;
			case SDL_Keycode.SDLK_1: return ImGuiKey._1;
			case SDL_Keycode.SDLK_2: return ImGuiKey._2;
			case SDL_Keycode.SDLK_3: return ImGuiKey._3;
			case SDL_Keycode.SDLK_4: return ImGuiKey._4;
			case SDL_Keycode.SDLK_5: return ImGuiKey._5;
			case SDL_Keycode.SDLK_6: return ImGuiKey._6;
			case SDL_Keycode.SDLK_7: return ImGuiKey._7;
			case SDL_Keycode.SDLK_8: return ImGuiKey._8;
			case SDL_Keycode.SDLK_9: return ImGuiKey._9;
			case SDL_Keycode.SDLK_A: return ImGuiKey.A;
			case SDL_Keycode.SDLK_B: return ImGuiKey.B;
			case SDL_Keycode.SDLK_C: return ImGuiKey.C;
			case SDL_Keycode.SDLK_D: return ImGuiKey.D;
			case SDL_Keycode.SDLK_E: return ImGuiKey.E;
			case SDL_Keycode.SDLK_F: return ImGuiKey.F;
			case SDL_Keycode.SDLK_G: return ImGuiKey.G;
			case SDL_Keycode.SDLK_H: return ImGuiKey.H;
			case SDL_Keycode.SDLK_I: return ImGuiKey.I;
			case SDL_Keycode.SDLK_J: return ImGuiKey.J;
			case SDL_Keycode.SDLK_K: return ImGuiKey.K;
			case SDL_Keycode.SDLK_L: return ImGuiKey.L;
			case SDL_Keycode.SDLK_M: return ImGuiKey.M;
			case SDL_Keycode.SDLK_N: return ImGuiKey.N;
			case SDL_Keycode.SDLK_O: return ImGuiKey.O;
			case SDL_Keycode.SDLK_P: return ImGuiKey.P;
			case SDL_Keycode.SDLK_Q: return ImGuiKey.Q;
			case SDL_Keycode.SDLK_R: return ImGuiKey.R;
			case SDL_Keycode.SDLK_S: return ImGuiKey.S;
			case SDL_Keycode.SDLK_T: return ImGuiKey.T;
			case SDL_Keycode.SDLK_U: return ImGuiKey.U;
			case SDL_Keycode.SDLK_V: return ImGuiKey.V;
			case SDL_Keycode.SDLK_W: return ImGuiKey.W;
			case SDL_Keycode.SDLK_X: return ImGuiKey.X;
			case SDL_Keycode.SDLK_Y: return ImGuiKey.Y;
			case SDL_Keycode.SDLK_Z: return ImGuiKey.Z;
			case SDL_Keycode.SDLK_F1: return ImGuiKey.F1;
			case SDL_Keycode.SDLK_F2: return ImGuiKey.F2;
			case SDL_Keycode.SDLK_F3: return ImGuiKey.F3;
			case SDL_Keycode.SDLK_F4: return ImGuiKey.F4;
			case SDL_Keycode.SDLK_F5: return ImGuiKey.F5;
			case SDL_Keycode.SDLK_F6: return ImGuiKey.F6;
			case SDL_Keycode.SDLK_F7: return ImGuiKey.F7;
			case SDL_Keycode.SDLK_F8: return ImGuiKey.F8;
			case SDL_Keycode.SDLK_F9: return ImGuiKey.F9;
			case SDL_Keycode.SDLK_F10: return ImGuiKey.F10;
			case SDL_Keycode.SDLK_F11: return ImGuiKey.F11;
			case SDL_Keycode.SDLK_F12: return ImGuiKey.F12;
			case SDL_Keycode.SDLK_F13: return ImGuiKey.F13;
			case SDL_Keycode.SDLK_F14: return ImGuiKey.F14;
			case SDL_Keycode.SDLK_F15: return ImGuiKey.F15;
			case SDL_Keycode.SDLK_F16: return ImGuiKey.F16;
			case SDL_Keycode.SDLK_F17: return ImGuiKey.F17;
			case SDL_Keycode.SDLK_F18: return ImGuiKey.F18;
			case SDL_Keycode.SDLK_F19: return ImGuiKey.F19;
			case SDL_Keycode.SDLK_F20: return ImGuiKey.F20;
			case SDL_Keycode.SDLK_F21: return ImGuiKey.F21;
			case SDL_Keycode.SDLK_F22: return ImGuiKey.F22;
			case SDL_Keycode.SDLK_F23: return ImGuiKey.F23;
			case SDL_Keycode.SDLK_F24: return ImGuiKey.F24;
			case SDL_Keycode.SDLK_AC_BACK: return ImGuiKey.AppBack;
			case SDL_Keycode.SDLK_AC_FORWARD: return ImGuiKey.AppForward;
			default: break;
		}
		return ImGuiKey.None;
	}
}