//
// TileCanvas.cs
//
// Copyright (C) 2004 Novell, Inc.
//

//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Text;
using Beagle.Util;
using Gecko;

namespace Beagle.Tile {

	public class TileCanvas : Gecko.WebControl {

		public event EventHandler PreRenderEvent;
		public event EventHandler PostRenderEvent;

		public TileCanvas () : base ()
		{
			OpenUri += OnOpenUri;
		}

		private void DispatchAction (string tile_id,
					     string action)
		{
			Tile t = GetTile (tile_id);
			
			if (t == null)
				return;


			MethodInfo info = t.GetType().GetMethod (action,
								 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
								 null,
								 CallingConventions.Any,
								 new Type[] {},
								 null);
			if (info == null) {
				Console.WriteLine ("Couldn't find method called {0}", action);
				return;
			}
			object[] attrs = info.GetCustomAttributes (false);
			foreach (object attr in attrs) {
				if (attr is TileActionAttribute) {
					info.Invoke (t, null);
					return;
				}
			}
			Console.WriteLine ("{0} does not have the TileAction attribute");
		}

		private void OnOpenUri (object o, OpenUriArgs args)
		{
			string uri = args.AURI;
			System.Console.WriteLine ("Open URI: {0}", uri);
			
			args.RetVal = true;

			if (DoAction (uri))
				return;

			if (uri.StartsWith ("action:")) {
				int pos1 = "action:".Length;
				int pos2 = uri.IndexOf ("!");
				if (pos2 > 0) {
					string tile_id = uri.Substring (pos1, pos2 - pos1);
					string action = uri.Substring (pos2 + 1);
					DispatchAction (tile_id, action);
				}
			}

			string command = null;
			string commandArgs = null;

			if (uri.StartsWith (Uri.UriSchemeHttp)
			    || uri.StartsWith (Uri.UriSchemeHttps)
			    || uri.StartsWith (Uri.UriSchemeFile)) {
				command = "gnome-open";
				commandArgs = "'" + uri + "'";
			} else if (uri.StartsWith (Uri.UriSchemeMailto)) {
				command = "evolution";
				commandArgs = uri;
			}

			if (command != null) {
				Process p = new Process ();
				p.StartInfo.UseShellExecute = false;
				p.StartInfo.FileName = command;
				if (args != null)
					p.StartInfo.Arguments = commandArgs;
				try {
					p.Start ();
				} catch (Exception e) {
					Console.WriteLine ("Unable to run {0}: {1}", command, e);
				}
				return;
			}

					
			return;
		}

		/////////////////////////////////////////////////

		Tile root = null;

		public Tile Root {
			get { return root; }
			set {
				root = value; 
				root.SetChangedHandler (new TileChangedHandler (OnTileChanged));
			}
		}

		/////////////////////////////////////////////////

		Hashtable actionTable = null;
		int actionId = 1;

		private void ClearActions ()
		{
			actionTable = new Hashtable ();
			actionId = 1;
		}
		
		private string AddAction (TileActionHandler handler)
		{
			if (handler == null)
				return "dynaction:NULL";
			string key = "dynaction:" + actionId.ToString ();
			++actionId;
			actionTable [key] = handler;
			return key;
		}

		private bool DoAction (string key)
		{
			TileActionHandler handler = (TileActionHandler) actionTable [key];
			if (handler != null) {
				handler ();
				return true;
			}
			return false;
		}

		/////////////////////////////////////////////////

		Hashtable tileTable = null;

		private void ClearTiles ()
		{
			tileTable = new Hashtable ();
		}

		private void CacheTile (Tile tile)
		{
			tileTable [tile.UniqueKey] = tile;
			tile.SetChangedHandler (new TileChangedHandler (OnTileChanged));
		}

		private Tile GetTile (string key)
		{
			if (key == "")
				return root;
			return (Tile) tileTable [key];
		}

		/////////////////////////////////////////////////

		private class TileCanvasRenderContext : TileRenderContext {

			TileCanvas canvas;
			Tile tileMain;

			public TileCanvasRenderContext (TileCanvas _canvas, Tile _tile)
			{
				canvas = _canvas;
				tileMain = _tile;
				canvas.CacheTile (tileMain);
			}

			override public void Write (string markup)
			{
				canvas.AppendData (markup);
			}

			override public void Link (string label, 
						   TileActionHandler handler)
			{
				string key = canvas.AddAction (handler);
				Write ("<a href=\"{0}\">{1}</a>", key, label);
			}

			override public void Tile (Tile tile)
			{
				canvas.CacheTile (tile);
				tile.Render (this);
			}
		}

		private static ArrayList style_attributes = null;
		private static ArrayList style_templates = null;
		private static string preferred_font_family;
		private static double preferred_font_size;

		static private void GetFontSettings ()
		{
			GConf.Client client = new GConf.Client ();
			string font = null;

			try {
				font = client.Get ("/desktop/gnome/interface/font_name") as string;
			} catch (GConf.NoSuchKeyException) { }

			// Sans 10 seems to be the default GNOME fallback
			if (font == null)
				font = "Sans 10";

			Pango.FontDescription font_desc = Pango.FontDescription.FromString (font);

			preferred_font_family = font_desc.Family;
			preferred_font_size = font_desc.Size / Pango.Scale.PangoScale;
		}

		static private void ScanAssembly (Assembly assembly)
		{
			style_attributes = new ArrayList ();
			foreach (Type type in assembly.GetTypes ()) {
				if (type.IsSubclassOf (typeof (Tile))) {
					foreach (object obj in Attribute.GetCustomAttributes (type)) {
						if (obj is TileStyleAttribute) {
							style_attributes.Add (obj);
						}
					}
				}
			}
		}

		private void RenderStyles (TileRenderContext ctx)
		{
			if (style_attributes == null) 
				ScanAssembly (Assembly.GetExecutingAssembly ());

			if (style_templates == null) {
				GetFontSettings ();

				style_templates = new ArrayList ();
				foreach (TileStyleAttribute attr in style_attributes) {
					Template t = new Template (attr.Resource);
					t["FontFamily"] = preferred_font_family;
					t["FontSize"] = preferred_font_size.ToString ();
					style_templates.Add (t);
				}
			}
			
			foreach (Template t in style_templates) {
				ctx.Write (t.ToString ());
			}
		}

		private void PaintTile (Tile tile)
		{
			TileCanvasRenderContext ctx;
			ctx = new TileCanvasRenderContext (this, 
							   tile);

			// IMPORTANT!  The <meta> tag has to be in the first
			// chunk written to the stream or else the encoding
			// will be wrong!
			ctx.Write ("<html>\n<head>\n<meta http-equiv=\"content-type\" content=\"text/html; charset=UTF-8\">\n");

       			ctx.Write ("<style type=\"text/css\" media=\"screen\">\n");
			RenderStyles (ctx);
			ctx.Write ("</style>\n"); 

			ctx.Write ("</head>\n");

			if (tile != null) {
				tile.Render (ctx);
			}

			ctx.Write ("</html>");
		}
		
		/////////////////////////////////////////////////

		StringBuilder content;

		public String Source {
			get { return content.ToString (); }
		}

		public new void AppendData (string data)
		{
			content.Append (data);
		}
	
		/////////////////////////////////////////////////

		private void DoRender ()
		{
			if (time == null) {
				time = new Beagle.Util.Stopwatch ();
				time.Start ();
			}
			System.Console.WriteLine ("Rendering");
			if (PreRenderEvent != null)
				PreRenderEvent (this, new EventArgs ());
				
			ClearActions ();
			ClearTiles ();

			string mime_type = "text/html";
			if (Environment.GetEnvironmentVariable ("BEST_DEBUG_HTML") != null)
			    mime_type = "text/plain";

			content = new StringBuilder ();
			PaintTile (root);
			RenderData (content.ToString (), "file:///", mime_type);

			if (PostRenderEvent != null)
				PostRenderEvent (this, new EventArgs ());

			time.Stop ();
			System.Console.WriteLine ("Done Rendering: {0}",
						  time);
			time = null;


		}

		/////////////////////////////////////////////////

		private uint renderId = 0;

		public void Render ()
		{
			lock (this) {
				if (renderId != 0) {
					GLib.Source.Remove (renderId);
					renderId = 0;
				}
				DoRender ();
			}
		}

		private bool RenderHandler ()
		{
			lock (this) {
				renderId = 0;
				DoRender ();
			}
			return false;
		}

		Beagle.Util.Stopwatch time;
		public void ScheduleRender ()
		{
			time = new Beagle.Util.Stopwatch ();
			time.Start ();
			lock (this) {
				if (renderId != 0)
					return;
				renderId = GLib.Idle.Add (new GLib.IdleHandler (RenderHandler));
			}
		}

		private void OnTileChanged (Tile tile)
		{
			ScheduleRender ();
		}
	}

}
