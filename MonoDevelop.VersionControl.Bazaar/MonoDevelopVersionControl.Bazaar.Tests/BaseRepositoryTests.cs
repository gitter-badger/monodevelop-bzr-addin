﻿using NUnit.Framework;
using System.IO;
using System;
using MonoDevelop.Core;
using MonoDevelop.Core.ProgressMonitoring;
using MonoDevelop.VersionControl;
using System.Collections.Generic;
using System.Linq;

namespace MonoDevelop.VersionControl.Tests
{
	[TestFixture]
	public abstract class BaseRepoUtilsTest
	{
		// [Git] Set user and email.
		protected const string Author = "author";
		protected const string Email = "email@service.domain";

		protected string RemoteUrl = "";
		protected FilePath RemotePath = "";
		protected FilePath LocalPath;
		protected Repository Repo;
		protected Repository Repo2;
		protected string DotDir;
		protected List<string> AddedItems = new List<string> ();
		protected int CommitNumber = 0;

		[SetUp]
		public abstract void Setup ();

		[TearDown]
		public virtual void TearDown ()
		{
			if (Repo != null) {
				Repo.Dispose ();
				Repo = null;
			}
			DeleteDirectory (RemotePath);
			DeleteDirectory (LocalPath);
			AddedItems.Clear ();
			CommitNumber = 0;
		}

		[Test]
		// Tests false positives of repository detection.
		public void IgnoreScatteredDotDir ()
		{
			var working = FileService.CreateTempDirectory ();

			var path = Path.Combine (working, "test");
			var staleGit = Path.Combine (working, ".git");
			var staleSvn = Path.Combine (working, ".svn");
			Directory.CreateDirectory (path);
			Directory.CreateDirectory (staleGit);
			Directory.CreateDirectory (staleSvn);

			Assert.IsNull (VersionControlService.GetRepositoryReference ((path).TrimEnd (Path.DirectorySeparatorChar), null));

			DeleteDirectory (working);
		}

		[Test]
		// Tests VersionControlService.GetRepositoryReference.
		public void RightRepositoryDetection ()
		{
			var path = ((string)LocalPath).TrimEnd (Path.DirectorySeparatorChar);
			var repo = VersionControlService.GetRepositoryReference (path, null);
			Assert.That (repo, IsCorrectType (), "#1");

			while (!String.IsNullOrEmpty (path)) {
				path = Path.GetDirectoryName (path);
				if (path == null)
					return;
				Assert.IsNull (VersionControlService.GetRepositoryReference (path, null), "#2." + path);
			}

			// Versioned file
			AddFile ("foo", "contents", true, true);
			path = Path.Combine (LocalPath, "foo");
			Assert.AreSame (VersionControlService.GetRepositoryReference (path, null), repo, "#2");

			// Versioned directory
			AddDirectory ("bar", true, true);
			path = Path.Combine (LocalPath, "bar");
			Assert.AreSame (VersionControlService.GetRepositoryReference (path, null), repo, "#3");

			// Unversioned file
			AddFile ("bip", "contents", false, false);
			Assert.AreSame (VersionControlService.GetRepositoryReference (path, null), repo, "#4");

			// Unversioned directory
			AddDirectory ("bop", false, false);
			Assert.AreSame (VersionControlService.GetRepositoryReference (path, null), repo, "#5");

			// Nonexistent file
			path = Path.Combine (LocalPath, "do_i_exist");
			Assert.AreSame (VersionControlService.GetRepositoryReference (path, null), repo, "#6");

			// Nonexistent directory
			path = Path.Combine (LocalPath, "do", "i", "exist");
			Assert.AreSame (VersionControlService.GetRepositoryReference (path, null), repo, "#6");
		}

		protected abstract NUnit.Framework.Constraints.IResolveConstraint IsCorrectType ();

		[Test]
		public void UrlIsValid ()
		{
			TestValidUrl ();
		}

		protected abstract void TestValidUrl ();

		[Test]
		// Tests Repository.Checkout.
		public void CheckoutExists ()
		{
			Assert.IsTrue (Directory.Exists (LocalPath + DotDir));
		}

		// In main directory, ".git".
		protected virtual int RepoItemsCount {
			get { return 0; }
		}

		// All contents of ".git".
		protected virtual int RepoItemsCountRecursive {
			get { return 0; }
		}

		// Subversion does an initial query.
		protected virtual VersionStatus InitialValue {
			get { return VersionStatus.Versioned; }
		}

		protected int QueryTimer {
			get { return 1000; }
		}

		[Test]
		// Tests Repository.GetVersionInfo with query thread.
		public void QueryThreadWorks ()
		{
			// Cache is initially empty.
			AddFile ("testfile", null, true, false);

			// Query two queries.
			VersionInfo vi = Repo.GetVersionInfo (LocalPath + "testfile");
			VersionInfo[] vis = Repo.GetDirectoryVersionInfo (LocalPath, false, false);

			// No cache, query.
			Assert.AreEqual (InitialValue, vi.Status);
			Assert.AreEqual (0, vis.Length);
			System.Threading.Thread.Sleep (QueryTimer);

			// Cached.
			vi = Repo.GetVersionInfo (LocalPath + "testfile");
			Assert.AreEqual (VersionStatus.ScheduledAdd, vi.Status & VersionStatus.ScheduledAdd);

			AddDirectory ("testdir", true, false);
			AddFile (Path.Combine ("testdir", "testfile2"), null, true, false);

			// Old cache.
			vis = Repo.GetDirectoryVersionInfo (LocalPath, false, false);
			Assert.AreEqual (1 + RepoItemsCount, vis.Length, "Old DirectoryVersionInfo.");

			// Query.
			Repo.ClearCachedVersionInfo (LocalPath);
			Repo.GetDirectoryVersionInfo (LocalPath, false, false);
			System.Threading.Thread.Sleep (QueryTimer);

			// Cached.
			vis = Repo.GetDirectoryVersionInfo (LocalPath, false, false);
			Assert.AreEqual (2 + RepoItemsCount, vis.Length, "New DirectoryVersionInfo.");

			// Wait for result.
			AddFile ("testfile3", null, true, false);
			vis = Repo.GetDirectoryVersionInfo (LocalPath, false, true);
			Assert.AreEqual (4 + RepoItemsCountRecursive, vis.Length, "Recursive DirectoryVersionInfo.");
		}

		[Test]
		// Tests Repository.Add.
		public void FileIsAdded ()
		{
			AddFile ("testfile", null, true, false);

			VersionInfo vi = Repo.GetVersionInfo (LocalPath + "testfile", VersionInfoQueryFlags.IgnoreCache);

			Assert.AreEqual (VersionStatus.Versioned, (VersionStatus.Versioned & vi.Status));
			Assert.AreEqual (VersionStatus.ScheduledAdd, (VersionStatus.ScheduledAdd & vi.Status));
			Assert.IsFalse (vi.CanAdd);
		}

		[Test]
		// Tests Repository.Commit.
		public void FileIsCommitted ()
		{
			AddFile ("testfile", null, true, true);
			PostCommit (Repo);

			VersionInfo vi = Repo.GetVersionInfo (LocalPath + "testfile", VersionInfoQueryFlags.IncludeRemoteStatus | VersionInfoQueryFlags.IgnoreCache);
			// TODO: Fix Win32 Svn Remote status check.
			Assert.AreEqual (VersionStatus.Versioned, (VersionStatus.Versioned & vi.Status));
		}

		protected virtual void PostCommit (Repository repo)
		{
		}

		[Test]
		// Tests Repository.Update.
		public virtual void UpdateIsDone ()
		{
			AddFile ("testfile", null, true, true);
			PostCommit (Repo);

			// Checkout a second repository.
			FilePath second = new FilePath (FileService.CreateTempDirectory () + Path.DirectorySeparatorChar);
			Checkout (second, RemoteUrl);
			Repo2 = GetRepo (second, RemoteUrl);
			ModifyPath (Repo2, ref second);
			string added = second + "testfile2";
			File.Create (added).Close ();
			Repo2.Add (added, false, new NullProgressMonitor ());
			ChangeSet changes = Repo2.CreateChangeSet (Repo2.RootPath);
			changes.AddFile (Repo2.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache));
			changes.GlobalComment = "test2";
			Repo2.Commit (changes, new NullProgressMonitor ());

			PostCommit (Repo2);

			Repo.Update (Repo.RootPath, true, new NullProgressMonitor ());
			Assert.True (File.Exists (LocalPath + "testfile2"));

			Repo2.Dispose ();
			DeleteDirectory (second);
		}

		protected virtual void ModifyPath (Repository repo, ref FilePath old)
		{
		}

		[Test]
		// Tests Repository.GetHistory.
		public void LogIsProper ()
		{
			AddFile ("testfile", null, true, true);
			AddFile ("testfile2", null, true, true);
			int index = 0;
			foreach (Revision rev in Repo.GetHistory (LocalPath + "testfile", null)) {
				Assert.AreEqual (String.Format ("Commit #{0}", index++), rev.Message);
			}
		}

		[Test]
		// Tests Repository.GenerateDiff.
		public void DiffIsProper ()
		{
			AddFile ("testfile", null, true, true);
			File.AppendAllText (LocalPath + "testfile", "text");

			TestDiff ();
		}

		protected abstract void TestDiff ();

		[Test]
		// Tests Repository.Revert and Repository.GetBaseText.
		public void Reverts ()
		{
			string content = "text";
			AddFile ("testfile", null, true, true);
			string added = LocalPath + "testfile";

			// Force cache update.
			Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache);

			// Revert to head.
			File.WriteAllText (added, content);
			Repo.Revert (added, false, new NullProgressMonitor ());
			Assert.AreEqual (Repo.GetBaseText (added), File.ReadAllText (added));
		}

		[TestCase (true)]
		[TestCase (false)]
		// Tests Repository.Revert
		public void Reverts2 (bool stage)
		{
			AddFile ("init", null, true, true);

			string added = LocalPath + "testfile";
			AddFile ("testfile", "test", stage, false);

			// Force cache evaluation.
			Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache);

			Repo.Revert (added, false, new NullProgressMonitor ());
			Assert.AreEqual (VersionStatus.Unversioned, Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache).Status);
		}

		[Test]
		// Tests Repository.GetRevisionChanges.
		public void CorrectRevisionChanges ()
		{
			AddFile ("testfile", "text", true, true);
			// TODO: Extend and test each member and more types.
			foreach (var rev in Repo.GetRevisionChanges (GetHeadRevision ())) {
				Assert.AreEqual (RevisionAction.Add, rev.Action);
			}
		}

		protected abstract Revision GetHeadRevision ();

		[Test]
		// Tests Repository.RevertRevision.
		public virtual void RevertsRevision ()
		{
			if (!Repo.SupportsRevertRevision)
				Assert.Ignore ("No support for reverting a specific revision.");

			string added = LocalPath + "testfile2";
			AddFile ("testfile", "text", true, true);
			AddFile ("testfile2", "text2", true, true);
			Repo.RevertRevision (added, GetHeadRevision (), new NullProgressMonitor ());
			Assert.IsFalse (File.Exists (added));
		}

		[Test]
		// Tests Repository.MoveFile.
		public virtual void MovesFile ()
		{
			string src;
			string dst;
			VersionInfo srcVi;
			VersionInfo dstVi;

			// Versioned file.
			AddFile ("testfile", null, true, true);
			src = LocalPath + "testfile";
			dst = src + "2";
			Repo.MoveFile (src, dst, false, new NullProgressMonitor ());
			srcVi = Repo.GetVersionInfo (src, VersionInfoQueryFlags.IgnoreCache);
			dstVi = Repo.GetVersionInfo (dst, VersionInfoQueryFlags.IgnoreCache);
			const VersionStatus versionedStatus = VersionStatus.ScheduledDelete | VersionStatus.ScheduledReplace;
			Assert.AreNotEqual (VersionStatus.Unversioned, srcVi.Status & versionedStatus);
			Assert.AreEqual (VersionStatus.ScheduledAdd, dstVi.Status & VersionStatus.ScheduledAdd);

			// Just added file.
			AddFile ("addedfile", null, true, false);
			src = LocalPath + "addedfile";
			dst = src + "2";
			Repo.MoveFile (src, dst, false, new NullProgressMonitor ());
			srcVi = Repo.GetVersionInfo (src, VersionInfoQueryFlags.IgnoreCache);
			dstVi = Repo.GetVersionInfo (dst, VersionInfoQueryFlags.IgnoreCache);
			Assert.AreEqual (VersionStatus.Unversioned, srcVi.Status);
			Assert.AreEqual (VersionStatus.ScheduledAdd, dstVi.Status & VersionStatus.ScheduledAdd);

			// Non versioned file.
			AddFile ("unversionedfile", null, false, false);
			src = LocalPath + "unversionedfile";
			dst = src + "2";
			Repo.MoveFile (src, dst, false, new NullProgressMonitor ());
			srcVi = Repo.GetVersionInfo (src, VersionInfoQueryFlags.IgnoreCache);
			dstVi = Repo.GetVersionInfo (dst, VersionInfoQueryFlags.IgnoreCache);
			Assert.AreEqual (VersionStatus.Unversioned, srcVi.Status);
			Assert.AreEqual (VersionStatus.Unversioned, dstVi.Status);
		}

		[Test]
		// Tests Repository.MoveDirectory.
		public virtual void MovesDirectory ()
		{
			string srcDir = LocalPath.Combine ("test");
			string dstDir = LocalPath.Combine ("test2");
			string src = Path.Combine (srcDir, "testfile");
			string dst = Path.Combine (dstDir, "testfile");

			AddDirectory ("test", true, false);
			AddFile (Path.Combine ("test", "testfile"), null, true, true);

			Repo.MoveDirectory (srcDir, dstDir, false, new NullProgressMonitor ());
			VersionInfo srcVi = Repo.GetVersionInfo (src, VersionInfoQueryFlags.IgnoreCache);
			VersionInfo dstVi = Repo.GetVersionInfo (dst, VersionInfoQueryFlags.IgnoreCache);
			const VersionStatus expectedStatus = VersionStatus.ScheduledDelete | VersionStatus.ScheduledReplace;
			Assert.AreNotEqual (VersionStatus.Unversioned, srcVi.Status & expectedStatus);
			Assert.AreEqual (VersionStatus.ScheduledAdd, dstVi.Status & VersionStatus.ScheduledAdd);
		}

		void DeleteFileTestHelper (bool keepLocal)
		{
			VersionInfo vi;
			string added;
			string postFix = keepLocal ? "2" : "";
			// Versioned file.
			added = LocalPath.Combine ("testfile1") + postFix;
			AddFile ("testfile1" + postFix, null, true, true);
			Repo.DeleteFile (added, true, new NullProgressMonitor (), keepLocal);
			vi = Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache);
			Assert.AreEqual (VersionStatus.ScheduledDelete, vi.Status & VersionStatus.ScheduledDelete);
			Assert.AreEqual (keepLocal, File.Exists (added));

			// Just added file.
			added = LocalPath.Combine ("testfile2") + postFix;
			AddFile ("testfile2" + postFix, null, true, false);
			Repo.DeleteFile (added, true, new NullProgressMonitor (), keepLocal);
			vi = Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache);
			Assert.AreEqual (VersionStatus.Unversioned, vi.Status);
			Assert.AreEqual (keepLocal, File.Exists (added));

			// Non versioned file.
			added = LocalPath.Combine ("testfile3") + postFix;
			AddFile ("testfile3" + postFix, null, false, false);
			Repo.DeleteFile (added, true, new NullProgressMonitor (), keepLocal);
			vi = Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache);
			Assert.AreEqual (VersionStatus.Unversioned, vi.Status);
			Assert.AreEqual (keepLocal, File.Exists (added));
		}

		[TestCase(false)]
		[TestCase(true)]
		// Tests Repository.DeleteFile.
		public virtual void DeletesFile (bool keepLocal)
		{
			DeleteFileTestHelper (keepLocal);
		}

		void DeleteTestDirectoryHelper (bool keepLocal)
		{
			VersionInfo vi;
			string addedDir;
			string added;
			string postFix = keepLocal ? "2" : "";

			// Versioned directory.
			addedDir = LocalPath.Combine ("test1") + postFix;
			added = Path.Combine (addedDir, "testfile");
			AddDirectory ("test1" + postFix, true, false);
			AddFile (Path.Combine ("test1" + postFix, "testfile"), null, true, true);

			Repo.DeleteDirectory (addedDir, true, new NullProgressMonitor (), keepLocal);
			vi = Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache);
			Assert.AreEqual (VersionStatus.ScheduledDelete, vi.Status & VersionStatus.ScheduledDelete);
			Assert.AreEqual (keepLocal, File.Exists (added));

			// Just added directory.
			addedDir = LocalPath.Combine ("test2") + postFix;
			added = Path.Combine (addedDir, "testfile");
			AddDirectory ("test2" + postFix, true, false);
			AddFile (Path.Combine ("test2" + postFix, "testfile"), null, true, false);

			Repo.DeleteDirectory (addedDir, true, new NullProgressMonitor (), keepLocal);
			vi = Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache);
			Assert.AreEqual (VersionStatus.Unversioned, vi.Status);
			Assert.AreEqual (keepLocal, File.Exists (added));

			// Non versioned file.
			addedDir = LocalPath.Combine ("test3") + postFix;
			added = Path.Combine (addedDir, "testfile");
			AddDirectory ("test3" + postFix, true, false);
			AddFile (Path.Combine ("test3" + postFix, "testfile"), null, false, false);

			Repo.DeleteDirectory (addedDir, true, new NullProgressMonitor (), keepLocal);
			vi = Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache);
			Assert.AreEqual (VersionStatus.Unversioned, vi.Status);
			Assert.AreEqual (keepLocal, File.Exists (added));
		}

		[Test]
		// Tests Repository.DeleteDirectory.
		public virtual void DeletesDirectory ()
		{
			DeleteTestDirectoryHelper (false);
			DeleteTestDirectoryHelper (true);
		}

		[Test]
		// Tests Repository.Lock.
		public virtual void LocksEntities ()
		{
			string added = LocalPath + "testfile";
			AddFile ("testfile", null, true, true);
			Repo.Lock (new NullProgressMonitor (), added);

			PostLock ();
		}

		protected virtual void PostLock ()
		{
		}

		[Test]
		// Tests Repository.Unlock.
		public virtual void UnlocksEntities ()
		{
			string added = LocalPath + "testfile";
			AddFile ("testfile", null, true, true);
			Repo.Lock (new NullProgressMonitor (), "testfile");
			Repo.Unlock (new NullProgressMonitor (), added);

			PostLock ();
		}

		protected virtual void PostUnlock ()
		{
		}

		[Test]
		// Tests Repository.Ignore
		public virtual void IgnoresEntities ()
		{
			string added = LocalPath + "testfile";
			AddFile ("testfile", null, false, false);
			Repo.Ignore (new FilePath[] { added });
			VersionInfo vi = Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache);
			Assert.AreEqual (VersionStatus.Ignored, vi.Status & VersionStatus.Ignored);
		}

		[Test]
		// Tests Repository.Unignore
		public virtual void UnignoresEntities ()
		{
			string added = LocalPath + "testfile";
			AddFile ("testfile", null, false, false);
			Repo.Ignore (new FilePath[] { added });
			Repo.Unignore (new FilePath[] { added });
			VersionInfo vi = Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache);
			Assert.AreEqual (VersionStatus.Unversioned, vi.Status);
		}

		[Test]
		// TODO: Fix SvnSharp logic failing to generate correct URL.
		// Tests Repository.GetTextAtRevision.
		public virtual void CorrectTextAtRevision ()
		{
			string added = LocalPath + "testfile";
			AddFile ("testfile", "text1", true, true);
			File.AppendAllText (added, "text2");
			CommitFile (added);
			string text = Repo.GetTextAtRevision (added, GetHeadRevision ());
			Assert.AreEqual ("text1text2", text);
		}

		[Test]
		// Tests Repository.GetAnnotations.
		public void BlameIsCorrect ()
		{
			string added = LocalPath.Combine ("testfile");
			// Initial commit.
			AddFile ("testfile", "blah" + Environment.NewLine, true, true);
			// Second commit.
			File.AppendAllText (added, "wut" + Environment.NewLine);
			CommitFile (added);
			// Working copy.
			File.AppendAllText (added, "wut2" + Environment.NewLine);

			var annotations = Repo.GetAnnotations (added);
			for (int i = 0; i < 2; i++) {
				var annotation = annotations [i];
				Assert.IsTrue (annotation.HasDate);
				Assert.IsNotNull (annotation.Date);
			}

			BlameExtraInternals (annotations);

			Assert.False (annotations [2].HasEmail);
			Assert.IsNotNull (annotations [2].Author);
			Assert.IsNull (annotations [2].Email);
			Assert.AreEqual (annotations [2].Revision, GettextCatalog.GetString ("working copy"));
			Assert.AreEqual (annotations [2].Author, "<uncommitted>");
		}

		protected abstract void BlameExtraInternals (Annotation [] annotations);

		[Test]
		// Tests bug #23275
		public void MoveAndMoveBack ()
		{
			string added = LocalPath.Combine ("testfile");
			string dir = LocalPath.Combine ("testdir");
			string dirFile = Path.Combine (dir, "testfile");
			AddFile ("testfile", "test", true, true);
			AddDirectory ("testdir", true, false);
			Repo.MoveFile (added, dirFile, true, new NullProgressMonitor ());
			Repo.MoveFile (dirFile, added, true, new NullProgressMonitor ());

			Assert.AreEqual (VersionStatus.Unversioned, Repo.GetVersionInfo (dirFile, VersionInfoQueryFlags.IgnoreCache).Status);
			Assert.AreEqual (VersionStatus.Versioned, Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache).Status);
		}

		[Test]
		public void RevertingADeleteMakesTheFileVersioned ()
		{
			var added = LocalPath.Combine ("testfile");
			AddFile ("testfile", "test", true, true);

			// Force cache update.
			Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache);

			Repo.DeleteFile (added, true, new NullProgressMonitor (), false);
			Repo.Revert (added, false, new NullProgressMonitor ());

			Assert.AreEqual (VersionStatus.Versioned, Repo.GetVersionInfo (added, VersionInfoQueryFlags.IgnoreCache).Status);
		}
		#region Util

		protected void Checkout (string path, string url)
		{
			Repository _repo = GetRepo (path, url);
			_repo.Checkout (path, true, new NullProgressMonitor ());
			if (Repo == null)
				Repo = _repo;
			else
				Repo2 = _repo;
		}

		protected void CommitItems ()
		{
			ChangeSet changes = Repo.CreateChangeSet (Repo.RootPath);
			foreach (var item in AddedItems) {
				changes.AddFile (Repo.GetVersionInfo (item, VersionInfoQueryFlags.IgnoreCache));
			}
			changes.GlobalComment = String.Format ("Commit #{0}", CommitNumber);
			Repo.Commit (changes, new NullProgressMonitor ());
			CommitNumber++;
		}

		protected void CommitFile (string path)
		{
			ChangeSet changes = Repo.CreateChangeSet (Repo.RootPath);

			// [Git] Needed by build bots.
			changes.ExtendedProperties.Add ("Git.AuthorName", "author");
			changes.ExtendedProperties.Add ("Git.AuthorEmail", "email@service.domain");

			changes.AddFile (Repo.GetVersionInfo (path, VersionInfoQueryFlags.IgnoreCache));
			changes.GlobalComment = String.Format ("Commit #{0}", CommitNumber);
			Repo.Commit (changes, new NullProgressMonitor ());
			CommitNumber++;
		}

		protected void AddFile (string path, string contents, bool toVcs, bool commit)
		{
			AddToRepository (path, contents ?? "", toVcs, commit);
		}

		protected void AddDirectory (string path, bool toVcs, bool commit)
		{
			AddToRepository (path, null, toVcs, commit);
		}

		void AddToRepository (string relativePath, string contents, bool toVcs, bool commit)
		{
			string added = Path.Combine (LocalPath, relativePath);
			if (contents == null)
				Directory.CreateDirectory (added);
			else
				File.WriteAllText (added, contents);

			if (toVcs)
				Repo.Add (added, false, new NullProgressMonitor ());

			if (commit)
				CommitFile (added);
			else
				AddedItems.Add (added);
		}

		protected abstract Repository GetRepo (string path, string url);

		protected static void DeleteDirectory (string path)
		{
			string[] files = Directory.GetFiles (path);
			string[] dirs = Directory.GetDirectories (path);

			foreach (var file in files) {
				File.SetAttributes (file, FileAttributes.Normal);
				File.Delete (file);
			}

			foreach (var dir in dirs) {
				DeleteDirectory (dir);
			}

			Directory.Delete (path, true);
		}

		#endregion
	}
}