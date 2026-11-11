using System;
using System.Runtime.InteropServices;
using System.Text;
using GLFW;
using Exception = System.Exception;
using MatterHackers.RenderOpenGl.OpenGl;
using MatterHackers.VectorMath;
using ErrorCode = MatterHackers.RenderOpenGl.OpenGl.ErrorCode;

namespace MatterHackers.MatterControl.CrossPlatform
{
	/// <summary>
	/// A complete IOpenGL implementation that talks straight to the system OpenGL
	/// library. Entry points are looked up through glfwGetProcAddress, so the same
	/// code runs on macOS (OpenGL.framework, Intel and Apple silicon), Linux (libGL)
	/// and Windows (opengl32) without naming the library anywhere.
	///
	/// MatterControl draws with the fixed-function pipeline (glBegin, matrix stack,
	/// client arrays), so the window must be created with a legacy/compatibility
	/// context. On macOS that is the default 2.1 context GLFW creates when no
	/// version or profile hints are set.
	///
	/// Must be constructed on the thread that owns the current GL context.
	/// </summary>
	public sealed unsafe class NativeGL : IOpenGL
	{
		// OpenGL 1.1 - 1.5: required
		private readonly delegate* unmanaged[Cdecl]<uint, void> glBegin;
		private readonly delegate* unmanaged[Cdecl]<uint, uint, void> glBindBuffer;
		private readonly delegate* unmanaged[Cdecl]<uint, uint, void> glBindTexture;
		private readonly delegate* unmanaged[Cdecl]<uint, uint, void> glBlendFunc;
		private readonly delegate* unmanaged[Cdecl]<uint, nint, void*, uint, void> glBufferData;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glClear;
		private readonly delegate* unmanaged[Cdecl]<float, float, float, float, void> glClearColor;
		private readonly delegate* unmanaged[Cdecl]<double, void> glClearDepth;
		private readonly delegate* unmanaged[Cdecl]<byte, byte, byte, byte, void> glColor4ub;
		private readonly delegate* unmanaged[Cdecl]<byte, byte, byte, byte, void> glColorMask;
		private readonly delegate* unmanaged[Cdecl]<uint, uint, void> glColorMaterial;
		private readonly delegate* unmanaged[Cdecl]<int, uint, int, void*, void> glColorPointer;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glCullFace;
		private readonly delegate* unmanaged[Cdecl]<int, uint*, void> glDeleteBuffers;
		private readonly delegate* unmanaged[Cdecl]<int, uint*, void> glDeleteTextures;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glDepthFunc;
		private readonly delegate* unmanaged[Cdecl]<byte, void> glDepthMask;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glDisable;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glDisableClientState;
		private readonly delegate* unmanaged[Cdecl]<uint, int, int, void> glDrawArrays;
		private readonly delegate* unmanaged[Cdecl]<uint, int, uint, void*, void> glDrawElements;
		private readonly delegate* unmanaged[Cdecl]<uint, uint, uint, int, uint, void*, void> glDrawRangeElements;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glEnable;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glEnableClientState;
		private readonly delegate* unmanaged[Cdecl]<void> glEnd;
		private readonly delegate* unmanaged[Cdecl]<void> glFinish;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glFrontFace;
		private readonly delegate* unmanaged[Cdecl]<int, uint*, void> glGenBuffers;
		private readonly delegate* unmanaged[Cdecl]<int, uint*, void> glGenTextures;
		private readonly delegate* unmanaged[Cdecl]<uint> glGetError;
		private readonly delegate* unmanaged[Cdecl]<uint, byte*> glGetString;
		private readonly delegate* unmanaged[Cdecl]<uint, int, void*, void> glIndexPointer;
		private readonly delegate* unmanaged[Cdecl]<uint, uint, float*, void> glLightfv;
		private readonly delegate* unmanaged[Cdecl]<void> glLoadIdentity;
		private readonly delegate* unmanaged[Cdecl]<double*, void> glLoadMatrixd;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glMatrixMode;
		private readonly delegate* unmanaged[Cdecl]<float*, void> glMultMatrixf;
		private readonly delegate* unmanaged[Cdecl]<double, double, double, void> glNormal3d;
		private readonly delegate* unmanaged[Cdecl]<uint, int, void*, void> glNormalPointer;
		private readonly delegate* unmanaged[Cdecl]<double, double, double, double, double, double, void> glOrtho;
		private readonly delegate* unmanaged[Cdecl]<float, float, void> glPolygonOffset;
		private readonly delegate* unmanaged[Cdecl]<void> glPopAttrib;
		private readonly delegate* unmanaged[Cdecl]<void> glPopMatrix;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glPushAttrib;
		private readonly delegate* unmanaged[Cdecl]<void> glPushMatrix;
		private readonly delegate* unmanaged[Cdecl]<double, double, double, double, void> glRotated;
		private readonly delegate* unmanaged[Cdecl]<double, double, double, void> glScaled;
		private readonly delegate* unmanaged[Cdecl]<int, int, int, int, void> glScissor;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glShadeModel;
		private readonly delegate* unmanaged[Cdecl]<double, double, void> glTexCoord2d;
		private readonly delegate* unmanaged[Cdecl]<int, uint, int, void*, void> glTexCoordPointer;
		private readonly delegate* unmanaged[Cdecl]<uint, uint, float, void> glTexEnvf;
		private readonly delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void> glTexImage2D;
		private readonly delegate* unmanaged[Cdecl]<uint, uint, int, void> glTexParameteri;
		private readonly delegate* unmanaged[Cdecl]<double, double, double, void> glTranslated;
		private readonly delegate* unmanaged[Cdecl]<double, double, void> glVertex2d;
		private readonly delegate* unmanaged[Cdecl]<double, double, double, void> glVertex3d;
		private readonly delegate* unmanaged[Cdecl]<int, uint, int, void*, void> glVertexPointer;
		private readonly delegate* unmanaged[Cdecl]<int, int, int, int, void> glViewport;

		// OpenGL 2.0+ / extensions: optional. MatterControl itself does not use
		// these, only some agg-sharp samples do, so a missing entry point is not
		// an error until somebody calls it.
		private readonly delegate* unmanaged[Cdecl]<uint, void> glActiveTexture;
		private readonly delegate* unmanaged[Cdecl]<uint, uint, void> glAttachShader;
		private readonly delegate* unmanaged[Cdecl]<uint, uint, void> glBindFramebuffer;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glBindVertexArray;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glCompileShader;
		private readonly delegate* unmanaged[Cdecl]<uint> glCreateProgram;
		private readonly delegate* unmanaged[Cdecl]<uint, uint> glCreateShader;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glDeleteShader;
		private readonly delegate* unmanaged[Cdecl]<uint, uint, void> glDetachShader;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glEnableVertexAttribArray;
		private readonly delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void> glFramebufferTexture2D;
		private readonly delegate* unmanaged[Cdecl]<int, uint*, void> glGenFramebuffers;
		private readonly delegate* unmanaged[Cdecl]<int, uint*, void> glGenVertexArrays;
		private readonly delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> glGetShaderInfoLog;
		private readonly delegate* unmanaged[Cdecl]<uint, byte*, int> glGetUniformLocation;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glLinkProgram;
		private readonly delegate* unmanaged[Cdecl]<uint, int, byte**, int*, void> glShaderSource;
		private readonly delegate* unmanaged[Cdecl]<int, float, void> glUniform1f;
		private readonly delegate* unmanaged[Cdecl]<int, int, void> glUniform1i;
		private readonly delegate* unmanaged[Cdecl]<int, int, byte, float*, void> glUniformMatrix4fv;
		private readonly delegate* unmanaged[Cdecl]<uint, void> glUseProgram;
		private readonly delegate* unmanaged[Cdecl]<uint, int, uint, byte, int, void*, void> glVertexAttribPointer;

		public NativeGL()
		{
			glBegin = (delegate* unmanaged[Cdecl]<uint, void>)Required("glBegin");
			glBindBuffer = (delegate* unmanaged[Cdecl]<uint, uint, void>)Required("glBindBuffer", "glBindBufferARB");
			glBindTexture = (delegate* unmanaged[Cdecl]<uint, uint, void>)Required("glBindTexture");
			glBlendFunc = (delegate* unmanaged[Cdecl]<uint, uint, void>)Required("glBlendFunc");
			glBufferData = (delegate* unmanaged[Cdecl]<uint, nint, void*, uint, void>)Required("glBufferData", "glBufferDataARB");
			glClear = (delegate* unmanaged[Cdecl]<uint, void>)Required("glClear");
			glClearColor = (delegate* unmanaged[Cdecl]<float, float, float, float, void>)Required("glClearColor");
			glClearDepth = (delegate* unmanaged[Cdecl]<double, void>)Required("glClearDepth");
			glColor4ub = (delegate* unmanaged[Cdecl]<byte, byte, byte, byte, void>)Required("glColor4ub");
			glColorMask = (delegate* unmanaged[Cdecl]<byte, byte, byte, byte, void>)Required("glColorMask");
			glColorMaterial = (delegate* unmanaged[Cdecl]<uint, uint, void>)Required("glColorMaterial");
			glColorPointer = (delegate* unmanaged[Cdecl]<int, uint, int, void*, void>)Required("glColorPointer");
			glCullFace = (delegate* unmanaged[Cdecl]<uint, void>)Required("glCullFace");
			glDeleteBuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)Required("glDeleteBuffers", "glDeleteBuffersARB");
			glDeleteTextures = (delegate* unmanaged[Cdecl]<int, uint*, void>)Required("glDeleteTextures");
			glDepthFunc = (delegate* unmanaged[Cdecl]<uint, void>)Required("glDepthFunc");
			glDepthMask = (delegate* unmanaged[Cdecl]<byte, void>)Required("glDepthMask");
			glDisable = (delegate* unmanaged[Cdecl]<uint, void>)Required("glDisable");
			glDisableClientState = (delegate* unmanaged[Cdecl]<uint, void>)Required("glDisableClientState");
			glDrawArrays = (delegate* unmanaged[Cdecl]<uint, int, int, void>)Required("glDrawArrays");
			glDrawElements = (delegate* unmanaged[Cdecl]<uint, int, uint, void*, void>)Required("glDrawElements");
			glDrawRangeElements = (delegate* unmanaged[Cdecl]<uint, uint, uint, int, uint, void*, void>)Required("glDrawRangeElements", "glDrawRangeElementsEXT");
			glEnable = (delegate* unmanaged[Cdecl]<uint, void>)Required("glEnable");
			glEnableClientState = (delegate* unmanaged[Cdecl]<uint, void>)Required("glEnableClientState");
			glEnd = (delegate* unmanaged[Cdecl]<void>)Required("glEnd");
			glFinish = (delegate* unmanaged[Cdecl]<void>)Required("glFinish");
			glFrontFace = (delegate* unmanaged[Cdecl]<uint, void>)Required("glFrontFace");
			glGenBuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)Required("glGenBuffers", "glGenBuffersARB");
			glGenTextures = (delegate* unmanaged[Cdecl]<int, uint*, void>)Required("glGenTextures");
			glGetError = (delegate* unmanaged[Cdecl]<uint>)Required("glGetError");
			glGetString = (delegate* unmanaged[Cdecl]<uint, byte*>)Required("glGetString");
			glIndexPointer = (delegate* unmanaged[Cdecl]<uint, int, void*, void>)Required("glIndexPointer");
			glLightfv = (delegate* unmanaged[Cdecl]<uint, uint, float*, void>)Required("glLightfv");
			glLoadIdentity = (delegate* unmanaged[Cdecl]<void>)Required("glLoadIdentity");
			glLoadMatrixd = (delegate* unmanaged[Cdecl]<double*, void>)Required("glLoadMatrixd");
			glMatrixMode = (delegate* unmanaged[Cdecl]<uint, void>)Required("glMatrixMode");
			glMultMatrixf = (delegate* unmanaged[Cdecl]<float*, void>)Required("glMultMatrixf");
			glNormal3d = (delegate* unmanaged[Cdecl]<double, double, double, void>)Required("glNormal3d");
			glNormalPointer = (delegate* unmanaged[Cdecl]<uint, int, void*, void>)Required("glNormalPointer");
			glOrtho = (delegate* unmanaged[Cdecl]<double, double, double, double, double, double, void>)Required("glOrtho");
			glPolygonOffset = (delegate* unmanaged[Cdecl]<float, float, void>)Required("glPolygonOffset");
			glPopAttrib = (delegate* unmanaged[Cdecl]<void>)Required("glPopAttrib");
			glPopMatrix = (delegate* unmanaged[Cdecl]<void>)Required("glPopMatrix");
			glPushAttrib = (delegate* unmanaged[Cdecl]<uint, void>)Required("glPushAttrib");
			glPushMatrix = (delegate* unmanaged[Cdecl]<void>)Required("glPushMatrix");
			glRotated = (delegate* unmanaged[Cdecl]<double, double, double, double, void>)Required("glRotated");
			glScaled = (delegate* unmanaged[Cdecl]<double, double, double, void>)Required("glScaled");
			glScissor = (delegate* unmanaged[Cdecl]<int, int, int, int, void>)Required("glScissor");
			glShadeModel = (delegate* unmanaged[Cdecl]<uint, void>)Required("glShadeModel");
			glTexCoord2d = (delegate* unmanaged[Cdecl]<double, double, void>)Required("glTexCoord2d");
			glTexCoordPointer = (delegate* unmanaged[Cdecl]<int, uint, int, void*, void>)Required("glTexCoordPointer");
			glTexEnvf = (delegate* unmanaged[Cdecl]<uint, uint, float, void>)Required("glTexEnvf");
			glTexImage2D = (delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void>)Required("glTexImage2D");
			glTexParameteri = (delegate* unmanaged[Cdecl]<uint, uint, int, void>)Required("glTexParameteri");
			glTranslated = (delegate* unmanaged[Cdecl]<double, double, double, void>)Required("glTranslated");
			glVertex2d = (delegate* unmanaged[Cdecl]<double, double, void>)Required("glVertex2d");
			glVertex3d = (delegate* unmanaged[Cdecl]<double, double, double, void>)Required("glVertex3d");
			glVertexPointer = (delegate* unmanaged[Cdecl]<int, uint, int, void*, void>)Required("glVertexPointer");
			glViewport = (delegate* unmanaged[Cdecl]<int, int, int, int, void>)Required("glViewport");

			glActiveTexture = (delegate* unmanaged[Cdecl]<uint, void>)Optional("glActiveTexture", "glActiveTextureARB");
			glAttachShader = (delegate* unmanaged[Cdecl]<uint, uint, void>)Optional("glAttachShader");
			glBindFramebuffer = (delegate* unmanaged[Cdecl]<uint, uint, void>)Optional("glBindFramebuffer", "glBindFramebufferEXT");
			glBindVertexArray = (delegate* unmanaged[Cdecl]<uint, void>)Optional("glBindVertexArray", "glBindVertexArrayAPPLE");
			glCompileShader = (delegate* unmanaged[Cdecl]<uint, void>)Optional("glCompileShader");
			glCreateProgram = (delegate* unmanaged[Cdecl]<uint>)Optional("glCreateProgram");
			glCreateShader = (delegate* unmanaged[Cdecl]<uint, uint>)Optional("glCreateShader");
			glDeleteShader = (delegate* unmanaged[Cdecl]<uint, void>)Optional("glDeleteShader");
			glDetachShader = (delegate* unmanaged[Cdecl]<uint, uint, void>)Optional("glDetachShader");
			glEnableVertexAttribArray = (delegate* unmanaged[Cdecl]<uint, void>)Optional("glEnableVertexAttribArray");
			glFramebufferTexture2D = (delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void>)Optional("glFramebufferTexture2D", "glFramebufferTexture2DEXT");
			glGenFramebuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)Optional("glGenFramebuffers", "glGenFramebuffersEXT");
			glGenVertexArrays = (delegate* unmanaged[Cdecl]<int, uint*, void>)Optional("glGenVertexArrays", "glGenVertexArraysAPPLE");
			glGetShaderInfoLog = (delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void>)Optional("glGetShaderInfoLog");
			glGetUniformLocation = (delegate* unmanaged[Cdecl]<uint, byte*, int>)Optional("glGetUniformLocation");
			glLinkProgram = (delegate* unmanaged[Cdecl]<uint, void>)Optional("glLinkProgram");
			glShaderSource = (delegate* unmanaged[Cdecl]<uint, int, byte**, int*, void>)Optional("glShaderSource");
			glUniform1f = (delegate* unmanaged[Cdecl]<int, float, void>)Optional("glUniform1f");
			glUniform1i = (delegate* unmanaged[Cdecl]<int, int, void>)Optional("glUniform1i");
			glUniformMatrix4fv = (delegate* unmanaged[Cdecl]<int, int, byte, float*, void>)Optional("glUniformMatrix4fv");
			glUseProgram = (delegate* unmanaged[Cdecl]<uint, void>)Optional("glUseProgram");
			glVertexAttribPointer = (delegate* unmanaged[Cdecl]<uint, int, uint, byte, int, void*, void>)Optional("glVertexAttribPointer");
		}

		public bool GlHasBufferObjects => true;

		private static void* Optional(params string[] names)
		{
			foreach (var name in names)
			{
				var address = Glfw.GetProcAddress(name);
				if (address != IntPtr.Zero)
				{
					return (void*)address;
				}
			}

			return null;
		}

		private static void* Required(params string[] names)
		{
			var address = Optional(names);
			if (address == null)
			{
				throw new NotSupportedException(
					$"The OpenGL driver does not provide {names[0]}. {Program.AppName} needs a legacy (compatibility) OpenGL 2.1 context with fixed-function support.");
			}

			return address;
		}

		private static void Need(void* function, string name)
		{
			if (function == null)
			{
				throw new NotSupportedException($"The current OpenGL context does not provide {name}.");
			}
		}

		private static byte ToGl(bool value) => value ? (byte)1 : (byte)0;

		public void Begin(BeginMode mode) => glBegin((uint)mode);

		public void BindBuffer(int target, int buffer) => glBindBuffer((uint)target, (uint)buffer);

		public void BindFramebuffer(int target, int buffer)
		{
			Need(glBindFramebuffer, nameof(glBindFramebuffer));
			glBindFramebuffer((uint)target, (uint)buffer);
		}

		public void BindTexture(int target, int texture) => glBindTexture((uint)target, (uint)texture);

		public void BlendFunc(int sfactor, int dfactor) => glBlendFunc((uint)sfactor, (uint)dfactor);

		public void BufferData(int target, int size, IntPtr data, int usage) => glBufferData((uint)target, size, (void*)data, (uint)usage);

		public void Clear(int mask) => glClear((uint)mask);

		public void ClearColor(double r, double g, double b, double a) => glClearColor((float)r, (float)g, (float)b, (float)a);

		public void ClearDepth(double depth) => glClearDepth(depth);

		public void Color4(byte red, byte green, byte blue, byte alpha) => glColor4ub(red, green, blue, alpha);

		public void ColorMask(bool red, bool green, bool blue, bool alpha) => glColorMask(ToGl(red), ToGl(green), ToGl(blue), ToGl(alpha));

		public void ColorMaterial(MaterialFace face, ColorMaterialParameter mode) => glColorMaterial((uint)face, (uint)mode);

		public void ColorPointer(int size, ColorPointerType type, int stride, IntPtr pointer) => glColorPointer(size, (uint)type, stride, (void*)pointer);

		public void CullFace(CullFaceMode mode) => glCullFace((uint)mode);

		public void DeleteBuffer(int buffer)
		{
			uint name = (uint)buffer;
			glDeleteBuffers(1, &name);
		}

		public void DeleteTexture(int texture)
		{
			uint name = (uint)texture;
			glDeleteTextures(1, &name);
		}

		public void DepthFunc(int func) => glDepthFunc((uint)func);

		public void DepthMask(bool flag) => glDepthMask(ToGl(flag));

		public void Disable(int cap) => glDisable((uint)cap);

		public void DisableClientState(ArrayCap array) => glDisableClientState((uint)array);

		public void DrawArrays(BeginMode mode, int first, int count) => glDrawArrays((uint)mode, first, count);

		public void DrawElements(int mode, int count, int elementType, IntPtr indices) => glDrawElements((uint)mode, count, (uint)elementType, (void*)indices);

		public void DrawRangeElements(BeginMode mode, int start, int end, int count, DrawElementsType type, IntPtr indices)
			=> glDrawRangeElements((uint)mode, (uint)start, (uint)end, count, (uint)type, (void*)indices);

		public void Enable(int cap) => glEnable((uint)cap);

		public void EnableClientState(ArrayCap arrayCap) => glEnableClientState((uint)arrayCap);

		public void End() => glEnd();

		public void Finish() => glFinish();

		public void FrontFace(FrontFaceDirection mode) => glFrontFace((uint)mode);

		public int GenBuffer()
		{
			uint name = 0;
			glGenBuffers(1, &name);
			return (int)name;
		}

		public void GenBuffers(int n, out int buffer)
		{
			RequireSingle(n, nameof(GenBuffers));
			buffer = GenBuffer();
		}

		public int GenFramebuffer()
		{
			Need(glGenFramebuffers, nameof(glGenFramebuffers));
			uint name = 0;
			glGenFramebuffers(1, &name);
			return (int)name;
		}

		public void GenFramebuffers(int n, out int fbo)
		{
			RequireSingle(n, nameof(GenFramebuffers));
			fbo = GenFramebuffer();
		}

		public int GenTexture()
		{
			uint name = 0;
			glGenTextures(1, &name);
			return (int)name;
		}

		public void GenTextures(int n, out int textures)
		{
			RequireSingle(n, nameof(GenTextures));
			textures = GenTexture();
		}

		public void GenVertexArrays(int n, out int arrays)
		{
			RequireSingle(n, nameof(GenVertexArrays));
			Need(glGenVertexArrays, nameof(glGenVertexArrays));
			uint name = 0;
			glGenVertexArrays(1, &name);
			arrays = (int)name;
		}

		// The interface hands back a single name through an out parameter, so
		// asking for more than one would write past it.
		private static void RequireSingle(int n, string caller)
		{
			if (n != 1)
			{
				throw new ArgumentOutOfRangeException(nameof(n), $"{caller} can only create one object per call.");
			}
		}

		public ErrorCode GetError() => (ErrorCode)glGetError();

		public string GetString(StringName name)
		{
			byte* text = glGetString((uint)name);
			return text == null ? "" : Marshal.PtrToStringUTF8((IntPtr)text) ?? "";
		}

		public void IndexPointer(IndexPointerType type, int stride, IntPtr pointer) => glIndexPointer((uint)type, stride, (void*)pointer);

		public void Light(LightName light, LightParameter pname, float[] param)
		{
			fixed (float* values = param)
			{
				glLightfv((uint)light, (uint)pname, values);
			}
		}

		public void LoadIdentity() => glLoadIdentity();

		public void LoadMatrix(double[] m)
		{
			fixed (double* values = m)
			{
				glLoadMatrixd(values);
			}
		}

		public void MatrixMode(MatrixMode mode) => glMatrixMode((uint)mode);

		public void MultMatrix(float[] m)
		{
			fixed (float* values = m)
			{
				glMultMatrixf(values);
			}
		}

		public void Normal3(double x, double y, double z) => glNormal3d(x, y, z);

		public void NormalPointer(NormalPointerType type, int stride, IntPtr pointer) => glNormalPointer((uint)type, stride, (void*)pointer);

		public void Ortho(double left, double right, double bottom, double top, double zNear, double zFar) => glOrtho(left, right, bottom, top, zNear, zFar);

		public void PolygonOffset(float factor, float units) => glPolygonOffset(factor, units);

		public void PopAttrib() => glPopAttrib();

		public void PopMatrix() => glPopMatrix();

		public void PushAttrib(AttribMask mask) => glPushAttrib((uint)mask);

		public void PushMatrix() => glPushMatrix();

		public void Rotate(double angle, double x, double y, double z) => glRotated(angle, x, y, z);

		public void Scale(double x, double y, double z) => glScaled(x, y, z);

		public void Scissor(int x, int y, int width, int height) => glScissor(x, y, width, height);

		public void ShadeModel(ShadingModel model) => glShadeModel((uint)model);

		public void TexCoord2(double x, double y) => glTexCoord2d(x, y);

		public void TexCoordPointer(int size, TexCordPointerType type, int stride, IntPtr pointer) => glTexCoordPointer(size, (uint)type, stride, (void*)pointer);

		public void TexEnv(TextureEnvironmentTarget target, TextureEnvParameter pname, float param) => glTexEnvf((uint)target, (uint)pname, param);

		public void TexImage2D(int target, int level, int internalFormat, int width, int height, int border, int format, int type, byte[] pixels)
		{
			fixed (byte* data = pixels)
			{
				glTexImage2D((uint)target, level, internalFormat, width, height, border, (uint)format, (uint)type, data);
			}
		}

		public void TexParameter(TextureTarget target, TextureParameterName pname, int param) => glTexParameteri((uint)target, (uint)pname, param);

		public void TexParameteri(int target, int pname, int param) => glTexParameteri((uint)target, (uint)pname, param);

		public void Translate(Vector3 vector) => glTranslated(vector.X, vector.Y, vector.Z);

		public void Translate(double x, double y, double z) => glTranslated(x, y, z);

		public void Vertex2(double x, double y) => glVertex2d(x, y);

		public void Vertex3(double x, double y, double z) => glVertex3d(x, y, z);

		public void VertexPointer(int size, VertexPointerType type, int stride, IntPtr pointer) => glVertexPointer(size, (uint)type, stride, (void*)pointer);

		public void Viewport(int x, int y, int width, int height) => glViewport(x, y, width, height);

		public void ActiveTexture(int texture)
		{
			Need(glActiveTexture, nameof(glActiveTexture));
			glActiveTexture((uint)texture);
		}

		public void AttachShader(int program, int shader)
		{
			Need(glAttachShader, nameof(glAttachShader));
			glAttachShader((uint)program, (uint)shader);
		}

		public void BindVertexArray(int vertexArray)
		{
			Need(glBindVertexArray, nameof(glBindVertexArray));
			glBindVertexArray((uint)vertexArray);
		}

		public void CompileShader(int id)
		{
			Need(glCompileShader, nameof(glCompileShader));
			glCompileShader((uint)id);
		}

		public int CreateProgram()
		{
			Need(glCreateProgram, nameof(glCreateProgram));
			return (int)glCreateProgram();
		}

		public int CreateShader(int shaderType)
		{
			Need(glCreateShader, nameof(glCreateShader));
			return (int)glCreateShader((uint)shaderType);
		}

		public void DeleteShader(int shader)
		{
			Need(glDeleteShader, nameof(glDeleteShader));
			glDeleteShader((uint)shader);
		}

		public void DetachShader(int id, int shader)
		{
			Need(glDetachShader, nameof(glDetachShader));
			glDetachShader((uint)id, (uint)shader);
		}

		public void EnableVertexAttribArray(int index)
		{
			Need(glEnableVertexAttribArray, nameof(glEnableVertexAttribArray));
			glEnableVertexAttribArray((uint)index);
		}

		public void FramebufferTexture2D(int target, int attachment, int textarget, int texture, int level)
		{
			Need(glFramebufferTexture2D, nameof(glFramebufferTexture2D));
			glFramebufferTexture2D((uint)target, (uint)attachment, (uint)textarget, (uint)texture, level);
		}

		public string GetShaderInfoLog(int shader)
		{
			Need(glGetShaderInfoLog, nameof(glGetShaderInfoLog));
			const int Capacity = 4096;
			byte* log = stackalloc byte[Capacity];
			int length = 0;
			glGetShaderInfoLog((uint)shader, Capacity, &length, log);
			return length <= 0 ? "" : Encoding.UTF8.GetString(log, Math.Min(length, Capacity));
		}

		public int GetUniformLocation(int program, string name)
		{
			Need(glGetUniformLocation, nameof(glGetUniformLocation));
			fixed (byte* text = NullTerminated(name))
			{
				return glGetUniformLocation((uint)program, text);
			}
		}

		public void LinkProgram(int id)
		{
			Need(glLinkProgram, nameof(glLinkProgram));
			glLinkProgram((uint)id);
		}

		public void ShaderSource(int id, int count, string src, object p)
		{
			Need(glShaderSource, nameof(glShaderSource));
			fixed (byte* text = NullTerminated(src))
			{
				byte* line = text;
				glShaderSource((uint)id, 1, &line, null);
			}
		}

		public void Uniform1f(int location, float v0)
		{
			Need(glUniform1f, nameof(glUniform1f));
			glUniform1f(location, v0);
		}

		public void Uniform1i(int location, int v0)
		{
			Need(glUniform1i, nameof(glUniform1i));
			glUniform1i(location, v0);
		}

		public void UniformMatrix4fv(int location, int count, int transpose, float[] value)
		{
			Need(glUniformMatrix4fv, nameof(glUniformMatrix4fv));
			fixed (float* values = value)
			{
				glUniformMatrix4fv(location, count, (byte)(transpose != 0 ? 1 : 0), values);
			}
		}

		public void UseProgram(int program)
		{
			Need(glUseProgram, nameof(glUseProgram));
			glUseProgram((uint)program);
		}

		public void VertexAttribPointer(int index, int size, int type, int normalized, int stride, IntPtr pointer)
		{
			Need(glVertexAttribPointer, nameof(glVertexAttribPointer));
			glVertexAttribPointer((uint)index, size, (uint)type, (byte)(normalized != 0 ? 1 : 0), stride, (void*)pointer);
		}

		private static byte[] NullTerminated(string text)
		{
			var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
			Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
			return bytes;
		}
	}
}
