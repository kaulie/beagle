//
// QueryDriver.cs
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
using System.IO;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Threading;
using Beagle.Util;
namespace Beagle.Daemon {
	
	public class QueryDriver {

		// Contains list of queryables explicitly asked by --allow-backend or --backend name
		// --allow-backend/--backend name : dont read config information and only start backend 'name'
		static ArrayList excl_allowed_queryables = new ArrayList ();

		// Contains list of denied queryables from config/arguments (every queryable is enabled by default)
		// Unless overruled by --allow-backend/--backend name, deny backend only if names appears here.
		static ArrayList denied_queryables = new ArrayList ();
		
		static bool to_read_conf = true; // read backends from conf if true
		static bool done_reading_conf = false;

		static private void ReadBackendsFromConf ()
		{
			if (! to_read_conf || done_reading_conf)
				return;

			// set flag here to stop Allow() from calling ReadBackendsFromConf() again
			done_reading_conf = true;

			// To allow static indexes, "static" should be in allowed_queryables
			if (Conf.Daemon.AllowStaticBackend)
				Allow ("static");

			if (Conf.Daemon.DeniedBackends == null)
				return;
			
			foreach (string name in Conf.Daemon.DeniedBackends)
				denied_queryables.Add (name.ToLower ());
		}

		static public void OnlyAllow (string name)
		{
			excl_allowed_queryables.Add (name.ToLower ());
			to_read_conf = false;
		}
		
		static public void Allow (string name)
		{
			if (! done_reading_conf && to_read_conf)
				ReadBackendsFromConf ();

			denied_queryables.Remove (name.ToLower ());
		}
		
		static public void Deny (string name)
		{
			if (! done_reading_conf && to_read_conf)
				ReadBackendsFromConf ();

			name = name.ToLower ();
			if (!denied_queryables.Contains (name))
				denied_queryables.Add (name);
		}

		static private bool UseQueryable (string name)
		{
			name = name.ToLower ();

			if (excl_allowed_queryables.Contains (name))
				return true;
			if (excl_allowed_queryables.Count != 0)
				return false;

			if (denied_queryables.Contains (name))
				return false;

			return true;
		}

		//////////////////////////////////////////////////////////////////////////////////////

		// Paths to static queryables

		static ArrayList static_queryables = new ArrayList ();
		
		static public void AddStaticQueryable (string path) {

			if (! static_queryables.Contains (path))
				static_queryables.Add (path);
		}

		//////////////////////////////////////////////////////////////////////////////////////

		// Delay before starting the indexing process

		static int indexing_delay = 60;  // Default to 60 seconds

		public static int IndexingDelay {
			set { indexing_delay = value; }
		}

		//////////////////////////////////////////////////////////////////////////////////////

		// Use introspection to find all classes that implement IQueryable, the construct
		// associated Queryables objects.

		static ArrayList queryables = new ArrayList ();
		static Hashtable iqueryable_to_queryable = new Hashtable ();

		static bool ThisApiSoVeryIsBroken (Type m, object criteria)
		{
			return m == (Type) criteria;
		}

		static bool TypeImplementsInterface (Type t, Type iface)
		{
			Type[] impls = t.FindInterfaces (new TypeFilter (ThisApiSoVeryIsBroken),
							 iface);
			return impls.Length > 0;
		}

		// Find the types in the assembly that
		// (1) register themselves in AssemblyInfo.cs:IQueryableTypes and
		// (2) has a QueryableFlavor attribute attached
		// assemble a Queryable object and stick it into our list of queryables.
		static void ScanAssemblyForQueryables (Assembly assembly)
		{
			int count = 0;

			foreach (Type type in ReflectionFu.GetTypesFromAssemblyAttribute (assembly, typeof (IQueryableTypesAttribute))) {
				bool type_accepted = false;
				foreach (QueryableFlavor flavor in ReflectionFu.ScanTypeForAttribute (type, typeof (QueryableFlavor))) {
					if (! UseQueryable (flavor.Name))
						continue;

					if (flavor.RequireInotify && ! Inotify.Enabled) {
						Logger.Log.Warn ("Can't start backend '{0}' without inotify", flavor.Name);
						continue;
					}

					if (flavor.RequireExtendedAttributes && ! ExtendedAttribute.Supported) {
						Logger.Log.Warn ("Can't start backend '{0}' without extended attributes", flavor.Name);
						continue;
					}

					IQueryable iq = null;
					try {
						iq = Activator.CreateInstance (type) as IQueryable;
					} catch (Exception e) {
						Logger.Log.Error (e, "Caught exception while instantiating {0} backend", flavor.Name);
					}

					if (iq != null) {
						Queryable q = new Queryable (flavor, iq);
						queryables.Add (q);
						iqueryable_to_queryable [iq] = q;
						++count;
						type_accepted = true;
						break;
					}
				}

				if (! type_accepted)
					continue;

				object[] attributes = type.GetCustomAttributes (false);
				foreach (object attribute in attributes) {
					PropertyKeywordMapping mapping = attribute as PropertyKeywordMapping;
					if (mapping == null)
						continue;
					//Logger.Log.Debug (mapping.Keyword + " => " 
					//		+ mapping.PropertyName + 
					//		+ " is-keyword=" + mapping.IsKeyword + " (" 
					//		+ mapping.Description + ") "
					//		+ "(" + type.FullName + ")");
					PropertyKeywordFu.RegisterMapping (mapping);
				}
					
			}
			Logger.Log.Debug ("Found {0} backends in {1}", count, assembly.Location);
		}

		////////////////////////////////////////////////////////

		public static void ReadKeywordMappings ()
		{
			Logger.Log.Debug ("Reading mapping from filters");
			ArrayList assemblies = ReflectionFu.ScanEnvironmentForAssemblies ("BEAGLE_FILTER_PATH", PathFinder.FilterDir);

			foreach (Assembly assembly in assemblies) {
				foreach (Type type in ReflectionFu.GetTypesFromAssemblyAttribute (assembly, typeof (FilterTypesAttribute))) {
					object[] attributes = type.GetCustomAttributes (false);
					foreach (object attribute in attributes) {
						
						PropertyKeywordMapping mapping = attribute as PropertyKeywordMapping;
						if (mapping == null)
							continue;
						//Logger.Log.Debug (mapping.Keyword + " => " 
						//		+ mapping.PropertyName
						//		+ " is-keyword=" + mapping.IsKeyword + " (" 
						//		+ mapping.Description + ") "
						//		+ "(" + type.FullName + ")");
						PropertyKeywordFu.RegisterMapping (mapping);
					}
				}
			}
		}

		////////////////////////////////////////////////////////

		// Scans PathFinder.SystemIndexesDir after available 
		// system-wide indexes.
		static void LoadSystemIndexes () 
		{
			if (!Directory.Exists (PathFinder.SystemIndexesDir))
				return;
			
			Logger.Log.Info ("Loading system static indexes.");

			int count = 0;

			foreach (DirectoryInfo index_dir in new DirectoryInfo (PathFinder.SystemIndexesDir).GetDirectories ()) {
				if (! UseQueryable (index_dir.Name))
					continue;
				
				if (LoadStaticQueryable (index_dir, QueryDomain.System))
					count++;
			}

			Logger.Log.Info ("Found {0} system-wide indexes.", count);
		}

		// Scans configuration for user-specified index paths 
		// to load StaticQueryables from.
		static void LoadStaticQueryables () 
		{
			int count = 0;

			if (UseQueryable ("static")) {
				Logger.Log.Info ("Loading user-configured static indexes.");
				foreach (string path in Conf.Daemon.StaticQueryables)
					static_queryables.Add (path);
			}

			foreach (string path in static_queryables) {
				DirectoryInfo index_dir = new DirectoryInfo (StringFu.SanitizePath (path));

				if (!index_dir.Exists)
					continue;
				
				// FIXME: QueryDomain might be other than local
				if (LoadStaticQueryable (index_dir, QueryDomain.Local))
					count++;
			}

			Logger.Log.Info ("Found {0} user-configured static indexes..", count);
		}

		// Instantiates and loads a StaticQueryable from an index directory
		static private bool LoadStaticQueryable (DirectoryInfo index_dir, QueryDomain query_domain) 
		{
			StaticQueryable static_queryable = null;
			
			if (!index_dir.Exists)
				return false;
			
			try {
				static_queryable = new StaticQueryable (index_dir.Name, index_dir.FullName, true);
			} catch (InvalidOperationException) {
				Logger.Log.Warn ("Unable to create read-only index (likely due to index version mismatch): {0}", index_dir.FullName);
				return false;
			} catch (Exception e) {
				Logger.Log.Error (e, "Caught exception while instantiating static queryable: {0}", index_dir.Name);
				return false;
			}
			
			if (static_queryable != null) {
				QueryableFlavor flavor = new QueryableFlavor ();
				flavor.Name = index_dir.Name;
				flavor.Domain = query_domain;
				
				Queryable queryable = new Queryable (flavor, static_queryable);
				queryables.Add (queryable);
				
				iqueryable_to_queryable [static_queryable] = queryable;

				return true;
			}

			return false;
		}

		////////////////////////////////////////////////////////

		private static ArrayList assemblies = null;

		// Perform expensive initialization steps all at once.
		// Should be done before SignalHandler comes into play.
		static public void Init ()
		{
			ReadBackendsFromConf ();
			assemblies = ReflectionFu.ScanEnvironmentForAssemblies ("BEAGLE_BACKEND_PATH", PathFinder.BackendDir);
		}

		private static bool queryables_started = false;

		static public void Start ()
		{
			// Only add the executing assembly if we haven't already loaded it.
			if (assemblies.IndexOf (Assembly.GetExecutingAssembly ()) == -1)
				assemblies.Add (Assembly.GetExecutingAssembly ());

			foreach (Assembly assembly in assemblies) {
				ScanAssemblyForQueryables (assembly);

				// This allows backends to define their
				// own executors.
				Server.ScanAssemblyForExecutors (assembly);
			}
			
			assemblies = null;

			ReadKeywordMappings ();

			LoadSystemIndexes ();
			LoadStaticQueryables ();

			if (indexing_delay <= 0 || Environment.GetEnvironmentVariable ("BEAGLE_EXERCISE_THE_DOG") != null)
				StartQueryables ();
			else {
				Logger.Log.Debug ("Waiting {0} seconds before starting queryables", indexing_delay);
				GLib.Timeout.Add ((uint) indexing_delay * 1000, new GLib.TimeoutHandler (StartQueryables));
			}
		}

		static private bool StartQueryables ()
		{
			Logger.Log.Debug ("Starting queryables");

			foreach (Queryable q in queryables) {
				Logger.Log.Info ("Starting backend: '{0}'", q.Name);
				q.Start ();
			}

			queryables_started = true;

			return false;
		}

		static public string ListBackends ()
		{
			ArrayList assemblies = ReflectionFu.ScanEnvironmentForAssemblies ("BEAGLE_BACKEND_PATH", PathFinder.BackendDir);

			// Only add the executing assembly if we haven't already loaded it.
			if (assemblies.IndexOf (Assembly.GetExecutingAssembly ()) == -1)
				assemblies.Add (Assembly.GetExecutingAssembly ());

			string ret = "User:\n";

			foreach (Assembly assembly in assemblies) {
				foreach (Type type in ReflectionFu.GetTypesFromAssemblyAttribute (assembly, typeof (IQueryableTypesAttribute))) {
					foreach (QueryableFlavor flavor in ReflectionFu.ScanTypeForAttribute (type, typeof (QueryableFlavor)))
						ret += String.Format (" - {0}\n", flavor.Name);
				}
			}
			
			if (!Directory.Exists (PathFinder.SystemIndexesDir)) 
				return ret;
			
			ret += "System:\n";
			foreach (DirectoryInfo index_dir in new DirectoryInfo (PathFinder.SystemIndexesDir).GetDirectories ()) {
				ret += String.Format (" - {0}\n", index_dir.Name);
			}

			return ret;
		}

		static public Queryable GetQueryable (string name)
		{
			foreach (Queryable q in queryables) {
				if (q.Name == name)
					return q;
			}

			return null;
		}

		static public Queryable GetQueryable (IQueryable iqueryable)
		{
			return (Queryable) iqueryable_to_queryable [iqueryable];
		}

		////////////////////////////////////////////////////////

		public delegate void ChangedHandler (Queryable            queryable,
						     IQueryableChangeData changeData);

		static public event ChangedHandler ChangedEvent;

		// A method to fire the ChangedEvent event.
		static public void QueryableChanged (IQueryable           iqueryable,
						     IQueryableChangeData change_data)
		{
			if (ChangedEvent != null) {
				Queryable queryable = iqueryable_to_queryable [iqueryable] as Queryable;
				ChangedEvent (queryable, change_data);
			}
		}

		////////////////////////////////////////////////////////

		private class QueryClosure : IQueryWorker {

			Queryable queryable;
			Query query;
			IQueryResult result;
			IQueryableChangeData change_data;
			
			public QueryClosure (Queryable            queryable,
					     Query                query,
					     QueryResult          result,
					     IQueryableChangeData change_data)
			{
				this.queryable = queryable;
				this.query = query;
				this.result = result;
				this.change_data = change_data;
			}

			public void DoWork ()
			{
				queryable.DoQuery (query, result, change_data);
			}
		}

		static public void DoOneQuery (Queryable            queryable,
					       Query                query,
					       QueryResult          result,
					       IQueryableChangeData change_data)
		{
			if (queryable.AcceptQuery (query)) {
				QueryClosure qc = new QueryClosure (queryable, query, result, change_data);
				result.AttachWorker (qc);
			}
		}

		static void AddSearchTermInfo (QueryPart          part,
					       SearchTermResponse response, StringBuilder sb)
		{
			if (part.Logic == QueryPartLogic.Prohibited)
				return;

			if (part is QueryPart_Or) {
				ICollection sub_parts;
				sub_parts = ((QueryPart_Or) part).SubParts;
				foreach (QueryPart qp in sub_parts)
					AddSearchTermInfo (qp, response, sb);
				return;
			}

			if (! (part is QueryPart_Text))
				return;

			QueryPart_Text tp;
			tp = (QueryPart_Text) part;

			string [] split;
			split = tp.Text.Split (' ');
 
			// First, remove stop words
			for (int i = 0; i < split.Length; ++i)
				if (LuceneCommon.IsStopWord (split [i]))
					split [i] = null;

			// Assemble the phrase minus stop words
			sb.Length = 0;
			for (int i = 0; i < split.Length; ++i) {
				if (split [i] == null)
					continue;
				if (sb.Length > 0)
					sb.Append (' ');
				sb.Append (split [i]);
			}
			response.ExactText.Add (sb.ToString ());

			// Now assemble a stemmed version
			sb.Length = 0; // clear the previous value
			for (int i = 0; i < split.Length; ++i) {
				if (split [i] == null)
					continue;
				if (sb.Length > 0)
					sb.Append (' ');
				sb.Append (LuceneCommon.Stem (split [i]));
			}
			response.StemmedText.Add (sb.ToString ());
		}

		////////////////////////////////////////////////////////

		static private void DehumanizeQuery (Query query)
		{
			// We need to remap any QueryPart_Human parts into
			// lower-level part types.  First, we find any
			// QueryPart_Human parts and explode them into
			// lower-level types.
			ArrayList new_parts = null;
			foreach (QueryPart abstract_part in query.Parts) {
				if (abstract_part is QueryPart_Human) {
					QueryPart_Human human = abstract_part as QueryPart_Human;
					if (new_parts == null)
						new_parts = new ArrayList ();
					foreach (QueryPart sub_part in QueryStringParser.Parse (human.QueryString))
						new_parts.Add (sub_part);
				}
			}

			// If we found any QueryPart_Human parts, copy the
			// non-Human parts over and then replace the parts in
			// the query.
			if (new_parts != null) {
				foreach (QueryPart abstract_part in query.Parts) {
					if (! (abstract_part is QueryPart_Human))
						new_parts.Add (abstract_part);
				}
				
				query.ClearParts ();
				foreach (QueryPart part in new_parts)
					query.AddPart (part);
			}

		}

		static private SearchTermResponse AssembleSearchTermResponse (Query query)
		{
			StringBuilder sb = new StringBuilder ();
			SearchTermResponse search_term_response;
			search_term_response = new SearchTermResponse ();
			foreach (QueryPart part in query.Parts)
				AddSearchTermInfo (part, search_term_response, sb);
			return search_term_response;
		}

		static private void QueryEachQueryable (Query       query,
							QueryResult result)
		{
			// The extra pair of calls to WorkerStart/WorkerFinished ensures:
			// (1) that the QueryResult will fire the StartedEvent
			// and FinishedEvent, even if no queryable accepts the
			// query.
			// (2) that the FinishedEvent will only get called when all of the
			// backends have had time to finish.

			object dummy_worker = new object ();

			if (! result.WorkerStart (dummy_worker))
				return;
			
			foreach (Queryable queryable in queryables)
				DoOneQuery (queryable, query, result, null);
			
			result.WorkerFinished (dummy_worker);
		}
		
		static public void DoQueryLocal (Query       query,
						 QueryResult result)
		{
			DehumanizeQuery (query);

			SearchTermResponse search_term_response;
			search_term_response = AssembleSearchTermResponse (query);
			query.ProcessSearchTermResponse (search_term_response);

			QueryEachQueryable (query, result);
		}

		static public void DoQuery (Query                                query,
					    QueryResult                          result,
					    RequestMessageExecutor.AsyncResponse send_response)
		{
			DehumanizeQuery (query);

			SearchTermResponse search_term_response;
			search_term_response = AssembleSearchTermResponse (query);
			send_response (search_term_response);

			QueryEachQueryable (query, result);
		}

		////////////////////////////////////////////////////////

		static public IEnumerable GetIndexInformation ()
		{
			foreach (Queryable q in queryables)
				yield return q.GetQueryableStatus ();
		}

		////////////////////////////////////////////////////////

		static public bool IsIndexing {
			get {
				// If the backends haven't been started yet,
				// there is at least the initial setup.  Just
				// assume all the backends are indexing.
				if (! queryables_started)
					return true;

				foreach (Queryable q in queryables) {
					QueryableStatus status = q.GetQueryableStatus ();

					if (status == null)
						return false;

					if (status.IsIndexing)
						return true;
				}

				return false;
			}
		}
	}
}