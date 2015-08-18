using System;
using System.Collections.Generic;

using MonoDevelop.Core;
using System.IO;
using Gtk;

namespace MonoDevelop.VersionControl.Bazaar
{
	public class BazaarVersionControl : VersionControlSystem
	{
		private BazaarClient client;

		// TODO: Obtain this list from bzr.
		/// <value>
		/// Protocols supported by this addin
		/// </value>
		public static readonly string[] protocols = {
			"http", "https", "bzr", "bzr+ssh", "sftp", 
			"ftp", "file" 
		};

		public override string Name {
			get { return "Bazaar"; }
		}// Name

		public override bool IsInstalled {
			get {
				if (!installChecked) {
					installed = Client.CheckInstalled ();
					installChecked = true;
				}
				return installed;
			}
		}// IsInstalled
		private bool installed;
		private bool installChecked;

		/// <value>
		/// Bazaar client
		/// </value>
		public BazaarClient Client {
			get {
				if (null == client) { client = new BazaarCLibClient (); }
				return client;
			}
		}// Client

		public override IRepositoryEditor CreateRepositoryEditor (Repository repo)
		{
			return IsInstalled ?
				new UrlBasedRepositoryEditor ((BazaarRepository)repo):
				null;
		}// CreateRepositoryEditor

		protected override Repository OnCreateRepositoryInstance ()
		{
			return new BazaarRepository ();
		}// OnCreateRepositoryInstance

		public IList<string> List (string path, bool recurse, ListKind kind) {
			return Client.List (path, recurse, kind);
		}// List

		/// <summary>
		/// Gets the status of a version-controlled file
		/// </summary>
		/// <param name="repo">
		/// A <see cref="Repository"/> to which the file belongs
		/// </param>
		/// <param name="sourcefile">
		/// A <see cref="System.String"/>: The filename
		/// </param>
		/// <param name="getRemoteStatus">
		/// A <see cref="System.Boolean"/>: unused
		/// </param>
		/// <returns>
		/// A <see cref="VersionInfo"/> representing the file status
		/// </returns>
		private VersionInfo GetFileStatus (Repository repo, string sourcefile, bool getRemoteStatus)
		{
			IList<LocalStatus> statuses = Client.Status (sourcefile, null);

			if (null == statuses || statuses.Count == 0)
				throw new ArgumentException ("Path '" + sourcefile + "' does not exist in the repository.");

			return CreateNode (statuses[0], repo);
		}// GetFileStatus

		/// <summary>
		/// Create a VersionInfo from a LocalStatus
		/// </summary>
		private VersionInfo CreateNode (LocalStatus status, Repository repo) 
		{
			VersionStatus rs = VersionStatus.Unversioned;
			Revision rr = null;

			VersionStatus vstatus = ConvertStatus (status.Status);
			// System.Console.WriteLine ("Converted {0} to {1} for {2}", status.Status, vstatus, status.Filename);

			VersionInfo ret = new VersionInfo (status.Filename, Path.GetFullPath (status.Filename), Directory.Exists (status.Filename),
				vstatus, new BazaarRevision (repo, status.Revision),
				rs, rr);
			return ret;
		}// CreateNode

		/// <summary>
		/// Create a VersionInfo[] from an IList<LocalStatus>
		/// </summary>
		private VersionInfo[] CreateNodes (Repository repo, IList<LocalStatus> statuses) {
			List<VersionInfo> nodes = new List<VersionInfo> (statuses.Count);

			foreach (LocalStatus status in statuses) {
				nodes.Add (CreateNode (status, repo));
			}

			return nodes.ToArray ();
		}// CreateNodes


		/// <summary>
		/// Convert an ItemStatus to a VersionStatus
		/// </summary>
		private VersionStatus ConvertStatus (ItemStatus status) {
			switch (status) {
				case ItemStatus.Added:
					return VersionStatus.Versioned | VersionStatus.ScheduledAdd;
				case ItemStatus.Conflicted:
					return VersionStatus.Versioned | VersionStatus.Conflicted;
				case ItemStatus.Deleted:
					return VersionStatus.Versioned | VersionStatus.ScheduledDelete;
				case ItemStatus.Ignored:
					return VersionStatus.Versioned | VersionStatus.Ignored;
				case ItemStatus.Modified:
					return VersionStatus.Versioned | VersionStatus.Modified;
				case ItemStatus.Replaced:
					return VersionStatus.Versioned | VersionStatus.ScheduledReplace;
				case ItemStatus.Unchanged:
					return VersionStatus.Versioned;
			}

			return VersionStatus.Unversioned;
		}// ConvertStatus

		public override Repository GetRepositoryReference (FilePath path, string id)
		{
			// System.Console.WriteLine ("Requested repository reference for {0}", path);
			try {
				if (string.IsNullOrEmpty (BazaarRepository.GetLocalBasePath (path.FullPath)))
					return null;
				string url = Client.GetPathUrl (path.FullPath);
				// System.Console.WriteLine ("Got {0} for {1}", url, path);
				return new BazaarRepository (this, url);
			} catch (Exception ex) {
				// No bzr
				LoggingService.LogError (ex.ToString ());
				return null;
			}
		}// GetRepositoryReference

		public VersionInfo GetVersionInfo (Repository repo, string localPath, bool getRemoteStatus)
		{
			return GetFileStatus (repo, localPath, getRemoteStatus);
		}

		public VersionInfo[] GetDirectoryVersionInfo (Repository repo, string sourcepath, bool getRemoteStatus, bool recursive) {
			IList<LocalStatus> statuses = Client.Status (sourcepath, null);
			return CreateNodes (repo, statuses);
		}


		public void Update (string localPath, bool recurse, IProgressMonitor monitor) {
			Client.Update (localPath, recurse, monitor);
		}// Update

		public bool IsVersioned (string localPath)
		{
			return ((string.Empty != BazaarRepository.GetLocalBasePath (localPath)) && Client.IsVersioned (localPath));
		}

		public IList<LocalStatus> Status (string localPath, BazaarRevision revision) {
			return Client.Status (localPath, null);
		}// Status

		public void Revert (string localPath, bool recurse, IProgressMonitor monitor, BazaarRevision toRevision) {
			Client.Revert (localPath, recurse, monitor, toRevision);
		}// Revert

		public void Add (string localPath, bool recurse, IProgressMonitor monitor) {
			Client.Add (localPath, recurse, monitor);
			FileService.NotifyFileChanged (localPath);
		}// Add

		public void Checkout (string url, string targetLocalPath, BazaarRevision rev, bool recurse, IProgressMonitor monitor) {
			Client.Checkout (url, targetLocalPath, rev, recurse, monitor);
		}// Checkout

		public void Branch (string branchLocation, string localPath, IProgressMonitor monitor)
		{
			StoreCredentials (branchLocation);
			Client.Branch (branchLocation, localPath, monitor);
		}// Branch

		public string GetTextAtRevision (string path, BazaarRevision revision) {
			return Client.GetTextAtRevision (path, revision);
		}// GetTextAtRevision

		public Revision[] GetHistory (BazaarRepository repo, string localFile, Revision since) {
			BazaarRevision brev = (null == since)? new BazaarRevision (repo, BazaarRevision.FIRST): (BazaarRevision)since;
			return Client.GetHistory (repo, localFile, brev);
		}// GetHistory

		public void Merge (string mergeLocation, string localPath, bool remember, bool overwrite, BazaarRevision start, BazaarRevision end, IProgressMonitor monitor) {
			Client.Merge (mergeLocation, localPath, remember, overwrite, start, end, monitor);
		}// Merge

		public void Push (string pushLocation, string localPath, bool remember, bool overwrite, bool omitHistory, IProgressMonitor monitor) {
			if (omitHistory) {
				Client.DPush (pushLocation, localPath, remember, monitor);
			} else {
				Client.Push (pushLocation, localPath, remember, overwrite, monitor);
			}
		}// Push

		public void Pull (string pullLocation, string localPath, bool remember, bool overwrite, IProgressMonitor monitor) {
			Client.Pull (pullLocation, localPath, remember, overwrite, monitor);
		}// Pull

		public void Commit (ChangeSet changeSet, IProgressMonitor monitor) {
			if (Client.IsMergePending (changeSet.BaseLocalPath.FullPath)) {
				int result = (int)ResponseType.Cancel;
				MonoDevelop.Ide.DispatchService.GuiSyncDispatch(delegate{ 
					MessageDialog warningDialog = new MessageDialog (null, DialogFlags.Modal, MessageType.Warning, ButtonsType.OkCancel, 
						GettextCatalog.GetString ("Because there are merges pending, all pending changes must be committed together. Proceed?"));
					result = warningDialog.Run ();
					warningDialog.Destroy ();
				});// Warn user, see if she wants to proceed
				if ((int)ResponseType.Ok == result) {
					ChangeSet newChangeSet = changeSet.Repository.CreateChangeSet (changeSet.BaseLocalPath);
					newChangeSet.GlobalComment = changeSet.GlobalComment;
					changeSet = newChangeSet;
				} else {
					monitor.Log.WriteLine (GettextCatalog.GetString ("Aborted by user."));
					return;
				}
			}// if there are merges pending commit

			Client.Commit (changeSet, monitor);
			foreach (ChangeSetItem csi in changeSet.Items) {
				FileService.NotifyFileChanged (csi.LocalPath);
			}// Refresh file status
		}// Commit

		public DiffInfo[] Diff (string basePath, string[] files) {
			return Client.Diff (basePath, files);
		}// Diff

		public DiffInfo[] Diff (string path, BazaarRevision fromRevision, BazaarRevision toRevision)
		{
			return Client.Diff (path, fromRevision, toRevision);
		}// Diff

		public void Remove (string path, bool force, IProgressMonitor monitor) {
			Client.Remove (path, force, monitor);
		}// Remove

		public void Resolve (string path, bool recurse, IProgressMonitor monitor)
		{
			Client.Resolve (path, recurse, monitor);
			FileService.NotifyFileChanged (path);
		}// Resolve

		public Dictionary<string, BranchType> GetKnownBranches (string path)
		{
			return Client.GetKnownBranches (path);
		}// GetKnownBranches

		public void StoreCredentials (string url)
		{
			Client.StoreCredentials (url);
		}// StoreCredentials

		/// <summary>
		/// Initialize a new Bazaar repo
		/// </summary>
		/// <param name="newRepoPath">
		/// A <see cref="System.String"/>: The path at which to initialize a new repo
		/// </param>
		public void Init (string newRepoPath)
		{
			Client.Init (newRepoPath);
		}// Init

		public void Ignore (string path)
		{
			Client.Ignore (path);
		}// Ignore

		public bool IsBound (string path)
		{
			return Client.IsBound (path);
		}// IsBound

		public string GetBoundBranch (string path)
		{
			return Client.GetBoundBranch (path);
		}// GetBoundBranch

		public void Bind (string branchUrl, string localPath, IProgressMonitor monitor)
		{
			Client.Bind (branchUrl, localPath, monitor);
		}// Bind

		public void Unbind (string localPath, IProgressMonitor monitor)
		{
			Client.Unbind (localPath, monitor);
		}// Unbind

		public void Uncommit (string localPath, IProgressMonitor monitor)
		{
			Client.Uncommit (localPath, monitor);
		}// Uncommit

		public Annotation[] GetAnnotations (string localPath)
		{
			return Client.GetAnnotations (localPath);
		}// GetAnnotations

		public void Export (string localPath, string exportPath, IProgressMonitor monitor)
		{
			Client.Export (localPath, exportPath, monitor);
		}// Export

		public bool CanRebase ()
		{
			return Client.CanRebase ();
		}// CanRebase
	}// BazaarVersionControl
}