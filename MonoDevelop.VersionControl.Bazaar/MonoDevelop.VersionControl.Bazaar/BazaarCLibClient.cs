﻿using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MonoDevelop.Core;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Threading;

namespace MonoDevelop.VersionControl.Bazaar
{
	public class BazaarCLibClient : BazaarClient
	{
		#region " P/Invokes "

		[DllImport("python26")]
		private static extern void Py_Initialize();

		//		[DllImport ("python26")]
		//		private static extern void Py_Finalize ();

		[DllImport("python26")]
		private static extern void Py_DecRef(IntPtr pyobj);

		[DllImport("python26")]
		private static extern IntPtr PyImport_AddModule(string module);

		[DllImport("python26")]
		private static extern IntPtr PyModule_GetDict(IntPtr module);

		[DllImport("python26")]
		private static extern IntPtr PyMapping_GetItemString(IntPtr dict, string itemname);

		[DllImport("python26")]
		private static extern int PyRun_SimpleString(string command);

		[DllImport("python26")]
		private static extern int PyInt_AsLong(IntPtr pyint);

		[DllImport("python26")]
		private static extern int PyString_AsStringAndSize(IntPtr pystring, out IntPtr buffer, out int size);

		[DllImport("python26")]
		private static extern void PyErr_Clear();

		[DllImport("python26")]
		private static extern void PyEval_InitThreads();

		private static Regex unicodeRegex = new Regex(@"^\s*u'(?<realString>.*)'\s*$", RegexOptions.Compiled);

		private delegate string StringMarshaller(IntPtr pointer);

		private static StringMarshaller marshaller = (Platform.IsWindows) ? 
			(StringMarshaller)Marshal.PtrToStringAnsi : 
			(StringMarshaller)Marshal.PtrToStringAuto;

		/// <summary>
		/// Get a .NET string from a python string pointer.
		/// </summary>
		private static string StringFromPython(IntPtr pystring)
		{
			int size = 0;
			IntPtr buffer = IntPtr.Zero;
			string stringVal = null;

			if (IntPtr.Zero == pystring)
			{
				return string.Empty;
			}

			try
			{
				PyString_AsStringAndSize(pystring, out buffer, out size);
				stringVal = marshaller(buffer);
				if (string.IsNullOrEmpty(stringVal))
				{
					return string.Empty;
				}
			}
			finally
			{
				Py_DecRef(pystring);
			}

			Match match = unicodeRegex.Match(stringVal);
			if (match.Success)
			{
				stringVal = match.Groups["realString"].Value;
			}

			return stringVal;
		}
// StringFromPython

		/// <summary>
		/// Convenience wrapper for PyRun_SimpleString
		/// </summary>
		/// <param name="variables">
		/// A <see cref="List[System.String]"/>: The names of the return variables in command
		/// </param>
		/// <param name="command">
		/// A <see cref="System.String"/>: The command to run
		/// </param>
		/// <param name="format">
		/// A <see cref="System.Object[]"/>: string.Format()-style args for command
		/// </param>
		/// <returns>
		/// A <see cref="List[IntPtr]"/>: The values of variables, if any
		/// </returns>
		private static List<IntPtr> run(List<string> variables, string command, params object[] format)
		{
			List<IntPtr> rv = new List<IntPtr>();

			if (0 != PyRun_SimpleString(string.Format(command, format)))
			{
				string trace = "Unable to retrieve error data.";
				if (0 == PyRun_SimpleString("trace = ''.join(traceback.format_exception(sys.last_type, sys.last_value, sys.last_traceback))\n"))
				{
					trace = StringFromPython(PyMapping_GetItemString(maindict, "trace"));
				}
				PyErr_Clear();

				throw new BazaarClientException(string.Format("Error running '{0}': {1}{2}", string.Format(command, format), Environment.NewLine, trace));
			}

			if (null != variables)
			{
				rv = variables.ConvertAll<IntPtr>(delegate(string variable)
					{ 
						return PyMapping_GetItemString(maindict, variable);
					});
			}
			PyErr_Clear();

			return rv;
		}
// run

		#endregion


		public override string Version
		{
			get
			{
				if (null == version)
				{
					List<IntPtr> pychunks = null;
					int[] chunks = new int[3];
					lock (lockme)
					{
						try
						{
							pychunks = run(new List<string>{ "major", "minor", "other" }, "major,minor,other = bzrlib.api.get_current_api_version(object_with_api=None)");
							for (int i = 0; i < 3; ++i)
							{
								chunks[i] = PyInt_AsLong(pychunks[i]);
							}
						}
						finally
						{
							if (null != pychunks)
							{
								foreach (IntPtr chunk in pychunks)
								{
									Py_DecRef(chunk);
								}
							}
						}
					}
					version = string.Format("{0}.{1}.{2}", chunks[0], chunks[1], chunks[2]);
				}
				return version;
			}
		}

		static string version;

		private static IntPtr pymain;
		private static IntPtr maindict;
		private static readonly string lockme = "lockme";
		private static Regex UrlRegex = new Regex(@"^(?<protocol>[^:\s]+)://((?<username>[^:\s]+?)(:(?<password>[^@\s]+?))?@)?(?<host>[^:/\s]+)(:(?<port>\d+))?(?<path>/[^\s]*)$", RegexOptions.Compiled);

		static BazaarCLibClient()
		{
			try
			{
				PyEval_InitThreads();
				Py_Initialize();

				pymain = PyImport_AddModule("__main__");
				maindict = PyModule_GetDict(pymain);

				// Imports
				string[] imports = new string[]
				{
					"import sys",
					"if('win32'==sys.platform): sys.path.append('C:/Program Files/Bazaar/lib/library.zip')",
					"import traceback",
					"import StringIO",
					"import bzrlib",
					"from bzrlib import plugin",
					"bzrlib.plugin.load_plugins()",
					"from bzrlib import api",
					"from bzrlib import branch",
					"from bzrlib import workingtree",
					"from bzrlib import revisionspec",
					"from bzrlib import commit",
					"from bzrlib import diff",
					"from bzrlib import merge",
					"from bzrlib import bzrdir",
					"from bzrlib import log",
					"from bzrlib import revision",
					"from bzrlib import conflicts",
					"from bzrlib import config",
					"from bzrlib import transport",
					"from bzrlib import ignores",
					"from bzrlib import uncommit",
					"from bzrlib import annotate",
					"from bzrlib import builtins",
					"from bzrlib import commands",
					"from bzrlib import errors",
					"from bzrlib import foreign"
				};

				foreach (string import in imports)
				{
					run(null, import);
				}
			}
			catch (DllNotFoundException dnfe)
			{
				LoggingService.LogWarning("Unable to initialize BazaarCLibClient", dnfe);
			}
		}
// static constructor

		public BazaarCLibClient()
		{
		}

		public override void Add(string localPath, bool recurse, MonoDevelop.Core.IProgressMonitor monitor)
		{
			localPath = NormalizePath(Path.GetFullPath(localPath));
			StringBuilder command = new StringBuilder();
			command.AppendFormat("tree = workingtree.WorkingTree.open_containing(path=ur\"{0}\")[0]\n", localPath);
			command.AppendFormat(null, "tree.smart_add(file_list=[ur\"{0}\"], recurse={1})\n", localPath, recurse ? "True" : "False");

			lock (lockme)
			{
				run(null, command.ToString());
			}
		}

		public override void Branch(string branchLocation, string localPath, MonoDevelop.Core.IProgressMonitor monitor)
		{
			localPath = NormalizePath(Path.GetFullPath(localPath));
			if (null == monitor)
			{
				monitor = new MonoDevelop.Core.ProgressMonitoring.NullProgressMonitor();
			}
			string output = string.Empty;
			StringBuilder command = new StringBuilder();
			command.AppendFormat("mycmd = builtins.cmd_branch()\n");
			command.AppendFormat("mycmd.outf = StringIO.StringIO()\n");
			command.AppendFormat("try:\n");
			command.AppendFormat(string.Format("  mycmd.run(from_location=ur'{0}',to_location=ur'{1}')\n", branchLocation, localPath));
			command.AppendFormat("  output = mycmd.outf.getvalue()\n");
			command.AppendFormat("finally:\n");
			command.AppendFormat("  mycmd.outf.close()\n");

			lock (lockme)
			{
				output = StringFromPython(run(new List<string>{ "output" }, command.ToString())[0]);
			}

			monitor.Log.WriteLine(output);
			monitor.Log.WriteLine("Branched to {0}", localPath);
		}

		public override void Checkout(string url, string targetLocalPath, BazaarRevision rev, bool recurse, MonoDevelop.Core.IProgressMonitor monitor)
		{
			if (null == monitor)
			{
				monitor = new MonoDevelop.Core.ProgressMonitoring.NullProgressMonitor();
			}
			string pyrev = "None";
			string realUrl = url;
			StringBuilder command = new StringBuilder();

			Match match = UrlRegex.Match(url);
			if (match.Success)
			{
				realUrl = UrlRegex.Replace(url, @"${protocol}://${host}$3${path}");
			}

			lock (lockme)
			{
				run(null, "b = branch.Branch.open_containing(url=ur\"{0}\")[0]\n", realUrl);
			}

			monitor.Log.WriteLine("Opened {0}", url);

			if (null != rev && BazaarRevision.HEAD != rev.Rev && BazaarRevision.NONE != rev.Rev)
			{
				command.AppendFormat("revspec = revisionspec.RevisionSpec.from_string(spec=\"{0}\")\n", rev.Rev);
				pyrev = "revspec.in_history(branch=b).rev_id";
			}
			command.AppendFormat("b.create_checkout(to_location=ur\"{1}\", revision_id={0})\n", pyrev, NormalizePath(targetLocalPath));

			lock (lockme)
			{
				run(null, command.ToString());
			}
			monitor.Log.WriteLine("Checkout to {0} completed", targetLocalPath);
		}

		public override void Commit(ChangeSet changeSet, MonoDevelop.Core.IProgressMonitor monitor)
		{
			List<string> files = new List<string>();
			string basePath = BazaarRepository.GetLocalBasePath(changeSet.BaseLocalPath),
			pyfiles = string.Empty,
			escapedComment = changeSet.GlobalComment.Replace(Environment.NewLine, " ").Replace("\"", "\\\"");
			if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
			{
				basePath += Path.DirectorySeparatorChar;
			}

			foreach (ChangeSetItem item in changeSet.Items)
			{
				files.Add((((string)item.LocalPath.FullPath).Length <= basePath.Length) ? string.Empty : ((string)item.LocalPath.FullPath).Substring(basePath.Length));
			}

			if (0 == files.Count || (1 == files.Count && string.Empty.Equals(files[0], StringComparison.Ordinal)))
			{
				pyfiles = "None";
			}
			else
			{
				pyfiles = string.Format("[ur\"{0}\"]", string.Join("\",\"", files.ToArray()));
			}

			StringBuilder command = new StringBuilder();
			command.AppendFormat("tree = workingtree.WorkingTree.open_containing(path=ur\"{0}\")[0]\n", NormalizePath(basePath));
			command.AppendFormat("c = commit.Commit()\nc.commit(message=ur\"{0}\",specific_files={1},working_tree=tree)\n", escapedComment, pyfiles);

			lock (lockme)
			{
				run(null, command.ToString());
			}
		}

		public override DiffInfo[] Diff(string basePath, string[] files)
		{
			List<DiffInfo> results = new List<DiffInfo>();
			basePath = NormalizePath(Path.GetFullPath(basePath));

			if (null == files || 0 == files.Length)
			{
				if (Directory.Exists(basePath))
				{
					IList<LocalStatus> statuses = Status(basePath, new BazaarRevision(null, BazaarRevision.HEAD));
					List<string> foundFiles = new List<string>();
					foreach (LocalStatus status in statuses)
					{
						if (ItemStatus.Unchanged != status.Status)
						{
							foundFiles.Add(status.Filename);
						}
					}
					files = foundFiles.ToArray();
				}
				else
				{
					files = new string[]{ basePath };
				}
			}

			foreach (string file in files)
			{
				string fullPath = Path.Combine(basePath, file);
				StringBuilder command = new StringBuilder();
				command.AppendFormat("tree,relpath = workingtree.WorkingTree.open_containing(path=ur\"{0}\")\n", fullPath);
				command.AppendFormat("outfile = StringIO.StringIO()\n");
				command.AppendFormat("tree.lock_read()\ntry:\n");
				command.AppendFormat("  mydiff = bzrlib.diff.DiffTree(old_tree=tree.basis_tree(), new_tree=tree, to_file=outfile)\n");
				command.AppendFormat("  mydiff.show_diff(specific_files=[relpath])\n");
				command.AppendFormat("  output = outfile.getvalue()\n");
				command.AppendFormat("finally:\n  tree.unlock()\n  outfile.close()\n");

				lock (lockme)
				{
					string output = StringFromPython(run(new List<string>{ "output" }, command.ToString())[0]);
					results.Add(new DiffInfo(basePath, file, output.Replace("\r\n", Environment.NewLine)));
				}
			}

			return results.ToArray();
		}

		public override DiffInfo[] Diff(string path, BazaarRevision fromRevision, BazaarRevision toRevision)
		{
			List<DiffInfo> results = new List<DiffInfo>();
			path = NormalizePath(Path.GetFullPath(path));
			StringBuilder command = new StringBuilder();

			command.AppendFormat("outfile = StringIO.StringIO()\n");
			command.AppendFormat("old_tree = None\n");
			command.AppendFormat("new_tree = None\n");
			command.AppendFormat("try:\n");
			command.AppendFormat("  old_tree,new_tree,old_branch,new_branch,specific_files,extra_trees = diff.get_trees_and_branches_to_diff(path_list=None, revision_specs=[revisionspec.RevisionSpec.from_string(ur\"{0}\"), revisionspec.RevisionSpec.from_string(ur\"{1}\")], old_url=ur\"{2}\", new_url=ur\"{2}\")\n", 
				fromRevision, toRevision, path);
			command.AppendFormat("  mydiff = bzrlib.diff.DiffTree(old_tree=old_tree, new_tree=new_tree, to_file=outfile)\n");
			command.AppendFormat("  old_tree.lock_read()\n");
			command.AppendFormat("  new_tree.lock_read()\n");
			command.AppendFormat("  mydiff.show_diff(specific_files=specific_files, extra_trees=extra_trees)\n");
			command.AppendFormat("  output = outfile.getvalue()\n");
			command.AppendFormat("finally:\n");
			command.AppendFormat("  outfile.close()\n");
			command.AppendFormat("  if(old_tree): old_tree.unlock()\n");
			command.AppendFormat("  if(new_tree): new_tree.unlock()\n");

			lock (lockme)
			{
				string output = StringFromPython(run(new List<string>{ "output" }, command.ToString())[0]);
				results.Add(new DiffInfo(Path.GetDirectoryName(path), Path.GetFileName(path), 
						output.Replace("\r\n", Environment.NewLine)));
			}

			return results.ToArray();
		}

		static Regex revisionRegex = new Regex(@"^\s*(?<revision>[\d\.]+): (?<committer>.*) (?<date>\d{4}-\d{2}-\d{2}) (?<message>.*)", RegexOptions.Compiled);

		public override BazaarRevision[] GetHistory(BazaarRepository repo, string localFile, BazaarRevision since)
		{
			localFile = NormalizePath(Path.GetFullPath(localFile));
			List<BazaarRevision> history = new List<BazaarRevision>();
			string basePath = BazaarRepository.GetLocalBasePath(localFile);

			string output = null;
			string revString = "None";

			if (null != since && BazaarRevision.FIRST != since.Rev && BazaarRevision.NONE != since.Rev)
			{
				revString = string.Format("'{0}..'", since.Rev);
			}

			StringBuilder command = new StringBuilder();
			command.AppendFormat("mycmd = builtins.cmd_log()\n");
			command.AppendFormat("mycmd.outf = StringIO.StringIO()\n");
			command.AppendFormat("try:\n");
			command.AppendFormat(string.Format("  mycmd.run(file_list=[ur'{0}'],revision={1},log_format=log.log_formatter_registry.get('line'),include_merges=True)\n", 
					localFile, revString));

			command.AppendFormat("  output = mycmd.outf.getvalue()\n");
			command.AppendFormat("finally:\n");
			command.AppendFormat("  mycmd.outf.close()\n");

			lock (lockme)
			{
				output = StringFromPython(run(new List<string>{ "output" }, command.ToString())[0]);
			}

			Match match = null;
			foreach (string line in output.Split (new char[]{'\r','\n'}, StringSplitOptions.RemoveEmptyEntries))
			{
				match = revisionRegex.Match(line);
				if (null != match && match.Success)
				{
					DateTime date;
					DateTime.TryParse(match.Groups["date"].Value, out date);
					history.Add(new BazaarRevision(repo, match.Groups["revision"].Value, date,
							match.Groups["committer"].Value, match.Groups["message"].Value,
							new RevisionPath[]{ }));
				}
			}

			ThreadPool.QueueUserWorkItem(delegate
				{
					foreach (BazaarRevision rev in history)
					{
						Thread.Sleep(0);
						List<RevisionPath> paths = new List<RevisionPath>();
						foreach (LocalStatus status in Status (basePath, rev))
						{
							paths.Add(new RevisionPath(status.Filename, ConvertAction(status.Status), status.Status.ToString()));
						}
						rev.ChangedFiles = paths.ToArray();
					}
				});

			return history.ToArray();
		}
// GetHistory

		public override System.Collections.Generic.Dictionary<string, BranchType> GetKnownBranches(string path)
		{
			Dictionary<string, BranchType> branchGetters = new Dictionary<string, BranchType>
			{
				{ "get_parent", BranchType.Parent },
				{ "get_submit_branch", BranchType.Submit },
				{ "get_public_branch", BranchType.Public },
				{ "get_push_location", BranchType.Push }
			};
			Dictionary<string, BranchType> branches = new Dictionary<string, BranchType>();

			lock (lockme)
			{
				run(null, "b = branch.Branch.open_containing(url=ur\"{0}\")[0]\n", NormalizePath(Path.GetFullPath(path)));
				IntPtr branch = IntPtr.Zero;
				string mybranch;

				foreach (string getter in branchGetters.Keys)
				{
					try
					{
						branch = run(new List<string>{ "mybranch" }, "mybranch = b.{0}()", getter)[0];
						if (!string.IsNullOrEmpty(mybranch = StringFromPython(branch)))
						{
							branches[mybranch] = branchGetters[getter];
						}
					}
					catch
					{ 
						// Don't care, just means branch doesn't exist
					}
				}// invoke each getter
			}// lock

			return branches;
		}
// GetKnownBranches

		public override string GetPathUrl(string path)
		{
			IntPtr branch = IntPtr.Zero;

			lock (lockme)
			{
				branch = run(new List<string>{ "mybase" }, "mybranch = branch.Branch.open_containing(url=ur\"{0}\")[0]\nmybase=mybranch.base\n", NormalizePath(Path.GetFullPath(path)))[0];
				string baseurl = StringFromPython(branch);
				return baseurl.StartsWith("file://", StringComparison.Ordinal) ? baseurl.Substring(7) : baseurl;
			}
		}

		public override string GetTextAtRevision(string path, BazaarRevision rev)
		{
			StringBuilder command = new StringBuilder();
			IntPtr text = IntPtr.Zero;
			command.AppendFormat("revspec = revisionspec.RevisionSpec.from_string(spec=\"{0}\")\n", rev.Rev);
			command.AppendFormat("b,relpath = branch.Branch.open_containing(url=ur\"{0}\")\n", NormalizePath(path));
			command.AppendFormat("rev_tree = b.repository.revision_tree(revision_id=revspec.in_history(branch=b).rev_id)\n");
			command.AppendFormat("rev_tree.lock_read()\n");
			command.AppendFormat("try:\n");
			command.AppendFormat("  diff = rev_tree.get_file_text(file_id=rev_tree.path2id(path=relpath))\n");
			command.AppendFormat("finally:\n");
			command.AppendFormat("  rev_tree.unlock()\n");

			lock (lockme)
			{
				text = run(new List<string>{ "diff" }, command.ToString())[0];
				return StringFromPython(text); 
			}// lock
		}
// GetTextAtRevision

		public override bool IsVersioned(string path)
		{
			return base.IsVersioned(path);
		}

		public override System.Collections.Generic.IList<string> List(string path, bool recurse, ListKind kind)
		{
			List<string> found = new List<string>();
			List<IntPtr> pylist = null;
			string[] list = null;
			string relpath = string.Empty;

			StringBuilder command = new StringBuilder();
			command.AppendFormat("tree,relpath = workingtree.WorkingTree.open_containing(path=ur\"{0}\")\n", NormalizePath(path));
			command.AppendFormat("mylist = \"\"\ntree.lock_read()\n");
			command.AppendFormat("try:\n  for entry in tree.list_files():\n    mylist = mylist+entry[0]+\"|\"+entry[2]+\"\\n\"\n");
			command.AppendFormat("finally:\n  tree.unlock()\n");

			lock (lockme)
			{
				try
				{
					pylist = run(new List<string>{ "mylist", "relpath" }, command.ToString());
					list = StringFromPython(pylist[0]).Split('\n');
					relpath = StringFromPython(pylist[1]);
				}
				catch
				{
					return found;
				}
			}// lock

			foreach (string line in list)
			{
				string[] tokens = line.Split('|');
				if ((tokens[0].StartsWith(relpath, StringComparison.Ordinal)) &&
				    (ListKind.All == kind || listKinds[kind].Equals(tokens[1], StringComparison.Ordinal)) &&
				    (recurse || !tokens[0].Substring(relpath.Length).Contains("/")))
				{
					found.Add(tokens[0]);
				}// if valid match
			}

			return found;
		}
// List

		public override void Merge(string mergeLocation, string localPath, bool remember, bool overwrite, BazaarRevision start, BazaarRevision end, MonoDevelop.Core.IProgressMonitor monitor)
		{
			localPath = NormalizePath(Path.GetFullPath(localPath));
			if (null == monitor)
			{
				monitor = new MonoDevelop.Core.ProgressMonitoring.NullProgressMonitor();
			}

			string revisionSpec = string.Empty;
			string output = string.Empty;

			if (null != start && BazaarRevision.FIRST != start.Rev && BazaarRevision.NONE != start.Rev)
			{
				string endrev = "-1";
				if (null != end && BazaarRevision.HEAD != end.Rev && BazaarRevision.NONE != end.Rev)
				{
					endrev = end.Rev;
				}
				revisionSpec = string.Format("'{0}..{1}'", start.Rev, endrev);
			}
			else if (null != end && BazaarRevision.HEAD != end.Rev && BazaarRevision.NONE != end.Rev)
			{
				revisionSpec = string.Format("'1..{0}'", end.Rev);
			}// build revisionspec string

			StringBuilder command = new StringBuilder();
			command.AppendFormat("mycmd = builtins.cmd_merge()\n");
			command.AppendFormat("mycmd.outf = StringIO.StringIO()\n");
			command.AppendFormat("try:\n");
			command.AppendFormat(string.Format("  mycmd.run(location=ur'{0}',revision={1},force={4},remember={2},directory=ur'{3}')\n", 
					mergeLocation, string.IsNullOrEmpty(revisionSpec) ? "None" : revisionSpec, 
					remember ? "True" : "False", localPath, overwrite ? "True" : "False"));

			command.AppendFormat("  output = mycmd.outf.getvalue()\n");
			command.AppendFormat("finally:\n");
			command.AppendFormat("  mycmd.outf.close()\n");

			lock (lockme)
			{
				output = StringFromPython(run(new List<string>{ "output" }, command.ToString())[0]);
			}

			monitor.Log.WriteLine(output);

			monitor.Log.WriteLine("Merged to {0}", localPath);
		}

		public override void Pull(string pullLocation, string localPath, bool remember, bool overwrite, MonoDevelop.Core.IProgressMonitor monitor)
		{
			localPath = NormalizePath(Path.GetFullPath(localPath));
			if (null == monitor)
			{
				monitor = new MonoDevelop.Core.ProgressMonitoring.NullProgressMonitor();
			}
			string output = string.Empty;
			StringBuilder command = new StringBuilder();
			command.AppendFormat("mycmd = builtins.cmd_pull()\n");
			command.AppendFormat("mycmd.outf = StringIO.StringIO()\n");
			command.AppendFormat("try:\n");
			command.AppendFormat("  mycmd.run(location=ur'{0}',remember={1},overwrite={2},directory=ur'{3}',verbose=True)\n", pullLocation, remember ? "True" : "False", overwrite ? "True" : "False", localPath);
			command.AppendFormat("  output = mycmd.outf.getvalue()\n");
			command.AppendFormat("finally:\n");
			command.AppendFormat("  mycmd.outf.close()\n");

			lock (lockme)
			{
				output = StringFromPython(run(new List<string>{ "output" }, command.ToString())[0]);
			}

			monitor.Log.WriteLine(output);
			monitor.Log.WriteLine("Pulled to {0}", localPath);
		}

		public override void Push(string pushLocation, string localPath, bool remember, bool overwrite, MonoDevelop.Core.IProgressMonitor monitor)
		{
			localPath = NormalizePath(Path.GetFullPath(localPath));
			if (null == monitor)
			{
				monitor = new MonoDevelop.Core.ProgressMonitoring.NullProgressMonitor();
			}
			string output = string.Empty;
			StringBuilder command = new StringBuilder();
			command.AppendFormat("mycmd = builtins.cmd_push()\n");
			command.AppendFormat("mycmd.outf = StringIO.StringIO()\n");
			command.AppendFormat("try:\n");
			command.AppendFormat(string.Format("  mycmd.run(location=ur'{0}',remember={2},overwrite={3},directory=ur'{1}',strict=False)\n", 
					pushLocation, localPath, remember ? "True" : "False", overwrite ? "True" : "False"));
			command.AppendFormat("  output = mycmd.outf.getvalue()\n");
			command.AppendFormat("finally:\n");
			command.AppendFormat("  mycmd.outf.close()\n");

			lock (lockme)
			{
				output = StringFromPython(run(new List<string>{ "output" }, command.ToString())[0]);
			}

			monitor.Log.WriteLine(output);
			monitor.Log.WriteLine("Pushed to {0}", pushLocation);
		}

		public override void DPush(string pushLocation, string localPath, bool remember, MonoDevelop.Core.IProgressMonitor monitor)
		{
			localPath = NormalizePath(Path.GetFullPath(localPath));
			if (null == monitor)
			{
				monitor = new MonoDevelop.Core.ProgressMonitoring.NullProgressMonitor();
			}
			string output = string.Empty;
			StringBuilder command = new StringBuilder();
			command.AppendFormat("mycmd = foreign.cmd_dpush()\n");
			command.AppendFormat("mycmd.outf = StringIO.StringIO()\n");
			command.AppendFormat("try:\n");
			command.AppendFormat(string.Format("  mycmd.run(location=ur'{0}',remember={1}, directory=ur'{2}', strict=False)\n",
					pushLocation, remember ? "True" : "False",
					localPath));
			command.AppendFormat("  output = mycmd.outf.getvalue()\n");
			command.AppendFormat("finally:\n");
			command.AppendFormat("  mycmd.outf.close()\n");

			lock (lockme)
			{
				output = StringFromPython(run(new List<string>{ "output" }, command.ToString())[0]);
			}

			monitor.Log.WriteLine(output);
			monitor.Log.WriteLine("Pushed to {0}", pushLocation);
		}
// DPush

		public override void Remove(string path, bool force, MonoDevelop.Core.IProgressMonitor monitor)
		{
			path = NormalizePath(Path.GetFullPath(path));

			StringBuilder command = new StringBuilder();
			command.AppendFormat("tree,relpath = workingtree.WorkingTree.open_containing(path=ur\"{0}\")\n", path);
			command.AppendFormat("tree.remove(files=[relpath], force={1})\n", path, force ? "True" : "False");

			lock (lockme)
			{
				run(null, command.ToString());
			}
		}

		public override void Resolve(string path, bool recurse, MonoDevelop.Core.IProgressMonitor monitor)
		{
			path = NormalizePath(Path.GetFullPath(path));
			StringBuilder command = new StringBuilder();
			command.AppendFormat("tree,relpath = workingtree.WorkingTree.open_containing(path=ur\"{0}\")\n", path);
			command.AppendFormat("conflicts.resolve(tree=tree, paths=[relpath], recursive={1})\n", Path.GetFullPath(path), recurse ? "True" : "False");

			lock (lockme)
			{
				run(null, command.ToString());
			}
		}

		public override void Revert(string localPath, bool recurse, MonoDevelop.Core.IProgressMonitor monitor, BazaarRevision toRevision)
		{
			localPath = NormalizePath(Path.GetFullPath(localPath));

			StringBuilder command = new StringBuilder();
			command.AppendFormat("tree = workingtree.WorkingTree.open_containing(path=ur\"{0}\")[0]\n", localPath);
			if (null == toRevision || BazaarRevision.HEAD == toRevision.Rev || BazaarRevision.NONE == toRevision.Rev)
			{
				command.AppendFormat("rev = None\n");
			}
			else
			{
				command.AppendFormat("revspec = revisionspec.RevisionSpec.from_string(spec=\"{0}\")\n", toRevision.Rev);
				command.AppendFormat("b,relpath = branch.Branch.open_containing(url=ur\"{0}\")\n", localPath);
				command.AppendFormat("rev = b.repository.revision_tree(revision_id=revspec.in_history(branch=b).rev_id)\n");
			}
			command.AppendFormat("tree.lock_tree_write()\n");
			command.AppendFormat("try:\n  tree.revert(filenames=[tree.relpath(ur\"{0}\")], old_tree=rev)\n", localPath);
			command.AppendFormat("finally:\n  tree.unlock()\n");
			lock (lockme)
			{
				run(null, command.ToString());
			}
		}

		public override System.Collections.Generic.IList<LocalStatus> Status(string path, BazaarRevision revision)
		{
			StringBuilder command = new StringBuilder();
			List<LocalStatus> statuses = new List<LocalStatus>();
			LocalStatus mystatus = null;
			string rev = string.Empty;
			bool modified = false;
			IntPtr tuple = IntPtr.Zero,
			listlen = IntPtr.Zero;

			path = NormalizePath(Path.GetFullPath(path).Replace("{", "{{").Replace("}", "}}"));// escape for string.format
			command.AppendFormat("tree,relpath = workingtree.WorkingTree.open_containing(path=ur\"{0}\")\n", path);

			if (null == revision || BazaarRevision.HEAD == revision.Rev || BazaarRevision.NONE == revision.Rev)
			{
				command.AppendFormat("rev = tree.basis_tree()\n");
				command.AppendFormat("totree = tree\n");
				rev = BazaarRevision.HEAD;
			}
			else
			{
				command.AppendFormat("revspec = revisionspec.RevisionSpec.from_string(spec=\"{0}\")\n", ((BazaarRevision)revision.GetPrevious()).Rev);
				command.AppendFormat("rev = tree.branch.repository.revision_tree(revision_id=revspec.in_history(branch=tree.branch).rev_id)\n");
				command.AppendFormat("revspec = revisionspec.RevisionSpec.from_string(spec=\"{0}\")\n", revision.Rev);
				command.AppendFormat("totree = tree.branch.repository.revision_tree(revision_id=revspec.in_history(branch=tree.branch).rev_id)\n");
				rev = revision.Rev;
			}
			command.AppendFormat("status = totree.changes_from(other=rev, specific_files=[relpath])\n");

			lock (lockme)
			{
				run(null, command.ToString());

				string[] types = new string[]{ "added", "removed", "modified", "unversioned" };
				string filename;

				foreach (string modtype in types)
				{
					try
					{
						listlen = run(new List<string>{ "mylen" }, 
							"mylist = status.{0}\nmylen = len(mylist)\n", modtype)[0];
						int listlength = PyInt_AsLong(listlen);
						for (int i = 0; i < listlength; ++i)
						{
							tuple = run(new List<string>{ "astatus" }, "astatus = tree.abspath(filename=mylist[{0}][0])", i)[0];
							filename = StringFromPython(tuple);
							if (Platform.IsWindows)
							{
								filename = filename.Replace("/", "\\");
							}
							LocalStatus status = new LocalStatus(rev, filename, longStatuses[modtype]);
							if (path.Equals(filename, StringComparison.Ordinal))
							{
								mystatus = status;
							} 
							if (filename.StartsWith(path, StringComparison.Ordinal))
							{
								modified = (!"unversioned".Equals(modtype, StringComparison.Ordinal));
								statuses.Add(status);
							}
						}// get each file status
					}
					finally
					{
						Py_DecRef(listlen);
					}
				}

				command = new StringBuilder();
				command.Append("myconflicts = \"\"\n");
				command.Append("for conflict in totree.conflicts():\n");
				command.Append("  myconflicts = myconflicts + totree.abspath (filename=conflict.path) + \"|\"\n");

				string conflicts = StringFromPython(run(new List<string>{ "myconflicts" }, command.ToString())[0]);

				foreach (string conflict in conflicts.Split ('|'))
				{
					if (!string.IsNullOrEmpty(conflict))
					{
						bool matched = false;
						if (path.Equals(conflict, StringComparison.Ordinal))
						{
							if (null == mystatus)
							{
								statuses.Insert(0, mystatus = new LocalStatus(rev, path, ItemStatus.Conflicted));
							}
							else
							{
								mystatus.Status = ItemStatus.Conflicted;
							}
						}
						else if (Path.GetFullPath(conflict).StartsWith(path, StringComparison.Ordinal))
						{
							foreach (LocalStatus status in statuses)
							{
								if (conflict.EndsWith(status.Filename, StringComparison.Ordinal))
								{
									status.Status = ItemStatus.Conflicted;
									matched = true;
									break;
								}
							}// Check existing statuses
							if (!matched)
							{
								statuses.Add(new LocalStatus(rev, conflict, ItemStatus.Conflicted));
							}// Add new status if not found
						}// Child file is conflicted
					}// If conflict is valid path
				}// Check each conflict
			}// lock

			if (null == mystatus)
			{
				statuses.Insert(0, new LocalStatus("-1", path, modified ? ItemStatus.Modified : ItemStatus.Unchanged));
			}// path isn't in modified list

			return statuses;
		}

		public override void Update(string localPath, bool recurse, MonoDevelop.Core.IProgressMonitor monitor)
		{
			localPath = NormalizePath(Path.GetFullPath(localPath));
			lock (lockme)
			{
				run(null, "tree = workingtree.WorkingTree.open_containing(path=ur\"{0}\")[0]\ntree.update()\n", localPath);
			}
		}

		public override void StoreCredentials(string url)
		{
			try
			{
				Match match = UrlRegex.Match(url);
				if ((!url.StartsWith("lp:", StringComparison.Ordinal)) &&
				    match.Success &&
				    (match.Groups["username"].Success || match.Groups["password"].Success)) // No sense storing credentials with no username or password
				{ 
					string protocol = match.Groups["protocol"].Value.Trim();

					if ("sftp".Equals(protocol, StringComparison.OrdinalIgnoreCase) ||
					    protocol.EndsWith("ssh", StringComparison.OrdinalIgnoreCase))
					{
						protocol = "ssh";
					}

					run(null, "config.AuthenticationConfig().set_credentials(name='{0}', host='{1}', user='{2}', scheme='{3}', password={4}, port={5}, path='{6}', verify_certificates=False)", 
						UrlRegex.Replace(url, @"${protocol}://${host}$3${path}"),
						match.Groups["host"].Value,
						match.Groups["username"].Success ? match.Groups["username"].Value : string.Empty,
						protocol,
						(match.Groups["password"].Success && !"ssh".Equals(protocol, StringComparison.OrdinalIgnoreCase)) ? string.Format("'{0}'", match.Groups["password"].Value) : "None",
						match.Groups["port"].Success ? match.Groups["port"].Value : "None",
						match.Groups["path"].Value);
				}  // ignore LP urls

				System.Console.WriteLine("Stored credentials to {0}", Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".bazaar"), "authentication.conf"));
			}
			catch
			{
			} // Don't care
		}
// StoreCredentials

		public override void Init(string path)
		{
			StringBuilder command = new StringBuilder();
			command.Append("format = bzrdir.format_registry.make_bzrdir(key='default-rich-root')\n");
			command.AppendFormat("to_transport = transport.get_transport(base=ur'{0}')\n", NormalizePath(path));
			command.Append("create_branch = bzrdir.BzrDir.create_branch_convenience\n");
			command.Append("mybranch = create_branch(base=to_transport.base, format=format, possible_transports=[to_transport])\n");

			run(null, command.ToString());
		}
// Init

		public override void Ignore(string path)
		{
			StringBuilder command = new StringBuilder();
			command.AppendFormat("tree,relpath = workingtree.WorkingTree.open_containing(path=ur'{0}')\n", NormalizePath(path));
			command.AppendFormat("ignores.tree_ignores_add_patterns(tree=tree, name_pattern_list=[relpath])\n");

			run(null, command.ToString());
		}
// Ignore

		public override bool IsBound(string path)
		{
			StringBuilder command = new StringBuilder();
			command.AppendFormat("b = branch.Branch.open_containing(url=ur'{0}')[0]\n", NormalizePath(path));
			command.AppendFormat("bound = repr(b.get_bound_location())\n");

			return ("None" != StringFromPython(run(new List<string>{ "bound" }, command.ToString())[0]));
		}
// IsBound

		public override string GetBoundBranch(string path)
		{
			string method = (IsBound(path) ? "get_bound_location" : "get_old_bound_location");
			string location = string.Empty;

			StringBuilder command = new StringBuilder();
			command.AppendFormat("b = branch.Branch.open_containing(url=ur'{0}')[0]\n", NormalizePath(path));
			command.AppendFormat("bound = repr(b.{0}())\n", method);

			location = StringFromPython(run(new List<string>{ "bound" }, command.ToString())[0]);
			return ("None" == location ? string.Empty : location);
		}
// GetBoundBranch

		public override void Bind(string branchUrl, string localPath, MonoDevelop.Core.IProgressMonitor monitor)
		{
			run(null, "b = branch.Branch.open_containing(url=ur'{0}')[0]\n", localPath);
			monitor.Log.WriteLine("Opened {0}", NormalizePath(localPath));

			run(null, "remoteb = branch.Branch.open_containing(url=ur'{0}')[0]\n", branchUrl);
			monitor.Log.WriteLine("Opened {0}", branchUrl);

			run(null, "b.bind(other=remoteb)\n");
			monitor.Log.WriteLine("Bound {0} to {1}", localPath, branchUrl);
		}
// Bind

		public override void Unbind(string localPath, MonoDevelop.Core.IProgressMonitor monitor)
		{
			run(null, "b = branch.Branch.open_containing(url=ur'{0}')[0]\n", NormalizePath(localPath));
			monitor.Log.WriteLine("Opened {0}", localPath);

			run(null, "b.unbind()\n");
			monitor.Log.WriteLine("Unbound {0}", localPath);
		}
// Unbind

		public override void Uncommit(string localPath, MonoDevelop.Core.IProgressMonitor monitor)
		{
			run(null, "tree = workingtree.WorkingTree.open_containing(path=ur'{0}')[0]\nb = tree.branch\n", NormalizePath(localPath));
			monitor.Log.WriteLine("Opened {0}", localPath);

			run(null, "uncommit.uncommit(branch=b, tree=tree)\n");
			monitor.Log.WriteLine("Uncommit complete.");
		}
// Uncommit

		public override Annotation[] GetAnnotations(string localPath)
		{
			StringBuilder command = new StringBuilder();
			command.AppendFormat("tree,relpath = workingtree.WorkingTree.open_containing(path=ur'{0}')\n", NormalizePath(localPath));
			command.AppendFormat("try:\n");
			command.AppendFormat("  tree.lock_read()\n");
			command.AppendFormat("  id = tree.path2id(path=relpath)\n");
			command.AppendFormat("  f = StringIO.StringIO()\n");
			command.AppendFormat("  try:\n");
			command.AppendFormat("    annotate.annotate_file_tree(tree=tree, file_id=id, verbose=True, to_file=f)\n");
			command.AppendFormat("    annotations = f.getvalue()\n");
			command.AppendFormat("  finally:\n");
			command.AppendFormat("    f.close()\n");
			command.AppendFormat("finally:\n");
			command.AppendFormat("  tree.unlock()\n");

			string annotations = StringFromPython(run(new List<string>{ "annotations" }, command.ToString())[0]);

			string[] lines = annotations.Split(new string[]{ "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
			string[] tokens;
			char[] separators = new char[]{ ' ', '\t' };
			List<Annotation> result = new List<Annotation>();
			Annotation previous = new Annotation(string.Empty, string.Empty, DateTime.MinValue);

			foreach (string line in lines)
			{
				tokens = line.Split(separators, StringSplitOptions.RemoveEmptyEntries);
				if (2 < tokens.Length && !char.IsWhiteSpace(tokens[0][0]) && '|' != tokens[0][0])
				{
					previous = new Annotation(tokens[0], tokens[1],
						DateTime.ParseExact(tokens[2], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
				}
				result.Add(previous);
			}
			return result.ToArray();
		}
// GetAnnotations

		public override void Export(string localPath, string exportPath, MonoDevelop.Core.IProgressMonitor monitor)
		{
			localPath = NormalizePath(localPath);
			exportPath = NormalizePath(exportPath);
			if (!IsValidExportPath(exportPath))
			{
				throw new BazaarClientException(string.Format("Invalid export path: {0}", exportPath));
			}

			if (null == monitor)
			{
				monitor = new MonoDevelop.Core.ProgressMonitoring.NullProgressMonitor();
			}
			string output = string.Empty;
			StringBuilder command = new StringBuilder();
			command.AppendFormat("mycmd = builtins.cmd_export()\n");
			command.AppendFormat("mycmd.outf = StringIO.StringIO()\n");
			command.AppendFormat("try:\n");
			command.AppendFormat(string.Format("  mycmd.run(dest=ur'{0}',branch_or_subdir=ur'{1}')\n", exportPath, localPath));
			command.AppendFormat("  output = mycmd.outf.getvalue()\n");
			command.AppendFormat("finally:\n");
			command.AppendFormat("  mycmd.outf.close()\n");

			lock (lockme)
			{
				output = StringFromPython(run(new List<string>{ "output" }, command.ToString())[0]);
			}

			monitor.Log.WriteLine(output);
			monitor.Log.WriteLine("Exported to {0}", exportPath);
		}
// Export

		public override bool IsMergePending(string localPath)
		{
			lock (lockme)
			{ 
				// HACK: However, this is the way bzrlib does it.
				string pending = StringFromPython(run(new List<string>{ "pending" },
						                 "pending = str(len(workingtree.WorkingTree.open_containing(path=ur'{0}')[0].get_parent_ids()))",
						                 NormalizePath(localPath))[0]); 
				return !pending.Equals("1", StringComparison.Ordinal);
			}
		}
// IsMergePending

		public override bool CanRebase()
		{
			string haveRebase = null;
			StringBuilder command = new StringBuilder();
			command.AppendFormat("haveRebase = 'false'\n");
			command.AppendFormat("for name, plugin in bzrlib.plugin.plugins().items():\n");
			command.AppendFormat("  if(name == rebase):\n");
			command.AppendFormat("    haveRebase = 'true'\n");
			command.AppendFormat("    break\n");

			lock (lockme)
			{
				haveRebase = StringFromPython(run(new List<string>(){ "haveRebase" }, command.ToString())[0]); 
			}

			return "true".Equals(haveRebase, StringComparison.Ordinal);
		}
// CanRebase

		/**
		 * Needs update to rebase (See launchpad bug #459371)
		public void Rebase (string mergeLocation, string localPath, MonoDevelop.Core.IProgressMonitor monitor)
		{
			if (null == monitor){ monitor = new MonoDevelop.Core.ProgressMonitoring.NullProgressMonitor (); }
			string output = string.Empty;
			StringBuilder command = new StringBuilder ();
			command.AppendFormat ("mycmd = builtins.cmd_rebase()\n");
			command.AppendFormat ("mycmd.outf = StringIO.StringIO()\n");
			command.AppendFormat ("try:\n");
			command.AppendFormat (string.Format ("  mycmd.run(upstream_location='{0}', directory='{1}')\n", mergeLocation, localPath));
			command.AppendFormat ("  output = mycmd.outf.getvalue()\n");
			command.AppendFormat ("finally:\n");
			command.AppendFormat ("  mycmd.outf.close()\n");
			
			lock (lockme){ output = StringFromPython (run (new List<string>{"output"}, command.ToString ())[0]); }
			
			monitor.Log.WriteLine (output);
			monitor.Log.WriteLine ("Rebased to {0}", localPath);
		}// Rebase
		 */

		/// <summary>
		/// Normalize a local file path (primarily for windows)
		/// </summary>
		static string NormalizePath(string path)
		{
			string normalizedPath = path;

			if (Platform.IsWindows &&
			    !string.IsNullOrEmpty(normalizedPath) &&
			    normalizedPath.Trim().EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
			{
				normalizedPath = normalizedPath.Trim().Remove(normalizedPath.Length - 1);
			}// strip trailing backslash

			return normalizedPath;
		}
// NormalizePath
	}
// BazaarCLibClient
}

