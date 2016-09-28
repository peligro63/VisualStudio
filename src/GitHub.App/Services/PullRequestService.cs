using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using GitHub.Models;
using System.Reactive.Linq;
using Rothko;
using System.Text;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using GitHub.Primitives;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Reactive;
using System.Collections.Generic;
using LibGit2Sharp;

namespace GitHub.Services
{
    [NullGuard.NullGuard(NullGuard.ValidationFlags.None)]
    [Export(typeof(IPullRequestService))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class PullRequestService : IPullRequestService
    {
        static readonly Regex InvalidBranchCharsRegex = new Regex(@"[^0-9A-Za-z\-]", RegexOptions.ECMAScript);
        static readonly Regex BranchCapture = new Regex(@"branch\.(?<branch>.+)\.ghfvs-pr", RegexOptions.ECMAScript);

        static readonly string[] TemplatePaths = new[]
        {
            "PULL_REQUEST_TEMPLATE.md",
            "PULL_REQUEST_TEMPLATE",
            ".github\\PULL_REQUEST_TEMPLATE.md",
            ".github\\PULL_REQUEST_TEMPLATE",
        };

        readonly IGitClient gitClient;
        readonly IGitService gitService;
        readonly IOperatingSystem os;
        readonly IUsageTracker usageTracker;

        [ImportingConstructor]
        public PullRequestService(IGitClient gitClient, IGitService gitService, IOperatingSystem os, IUsageTracker usageTracker)
        {
            this.gitClient = gitClient;
            this.gitService = gitService;
            this.os = os;
            this.usageTracker = usageTracker;
        }

        public IObservable<IPullRequestModel> CreatePullRequest(IRepositoryHost host,
            ILocalRepositoryModel sourceRepository, IRepositoryModel targetRepository,
            IBranch sourceBranch, IBranch targetBranch,
            string title, string body
        )
        {
            Extensions.Guard.ArgumentNotNull(host, nameof(host));
            Extensions.Guard.ArgumentNotNull(sourceRepository, nameof(sourceRepository));
            Extensions.Guard.ArgumentNotNull(targetRepository, nameof(targetRepository));
            Extensions.Guard.ArgumentNotNull(sourceBranch, nameof(sourceBranch));
            Extensions.Guard.ArgumentNotNull(targetBranch, nameof(targetBranch));
            Extensions.Guard.ArgumentNotNull(title, nameof(title));
            Extensions.Guard.ArgumentNotNull(body, nameof(body));

            return PushAndCreatePR(host, sourceRepository, targetRepository, sourceBranch, targetBranch, title, body).ToObservable();
        }

        public IObservable<string> GetPullRequestTemplate(ILocalRepositoryModel repository)
        {
            Extensions.Guard.ArgumentNotNull(repository, nameof(repository));

            return Observable.Defer(() =>
            {
                var paths = TemplatePaths.Select(x => Path.Combine(repository.LocalPath, x));

                foreach (var path in paths)
                {
                    if (os.File.Exists(path))
                    {
                        try { return Observable.Return(os.File.ReadAllText(path, Encoding.UTF8)); } catch { }
                    }
                }
                return Observable.Empty<string>();
            });
        }

        public IObservable<Unit> FetchAndCheckout(ILocalRepositoryModel repository, int pullRequestNumber, string localBranchName)
        {
            return DoFetchAndCheckout(repository, pullRequestNumber, localBranchName).ToObservable();
        }

        public string GetDefaultLocalBranchName(int pullRequestNumber, string pullRequestTitle)
        {
            return "pr/" + pullRequestNumber + "-" + GetSafeBranchName(pullRequestTitle);
        }

        public IObservable<IBranch> GetLocalBranches(ILocalRepositoryModel repository, int number)
        {
            return Observable.Defer(() =>
            {
                var repo = gitService.GetRepository(repository.LocalPath);
                var result = GetLocalBranchesInternal(repo, number).Select(x => new BranchModel(x, repository));
                return result.ToObservable();
            });
        }

        public IObservable<Unit> SwitchToBranch(ILocalRepositoryModel repository, int number)
        {
            return Observable.Defer(() =>
            {
                var repo = gitService.GetRepository(repository.LocalPath);
                var branch = GetLocalBranchesInternal(repo, number).First();
                gitClient.Checkout(repo, branch);
                return Observable.Empty<Unit>();
            });
        }

        async Task DoFetchAndCheckout(ILocalRepositoryModel repository, int pullRequestNumber, string localBranchName)
        {
            var repo = gitService.GetRepository(repository.LocalPath);
            var configKey = $"branch.{BranchNameToConfigKey(localBranchName)}.ghfvs-pr";

            await gitClient.Fetch(repo, "origin", new[] { $"refs/pull/{pullRequestNumber}/head:{localBranchName}" });
            await gitClient.Checkout(repo, localBranchName);
            await gitClient.SetConfig(repo, configKey, pullRequestNumber.ToString());
        }

        IEnumerable<string> GetLocalBranchesInternal(IRepository repository, int number)
        {
            var pr = number.ToString();
            return repository.Config
                .Select(x => new { Branch = BranchCapture.Match(x.Key).Groups["branch"].Value, Value = x.Value })
                .Where(x => !string.IsNullOrWhiteSpace(x.Branch) && x.Value == pr)
                .Select(x => ConfigKeyToBranchName(x.Branch));
        }

        async Task<IPullRequestModel> PushAndCreatePR(IRepositoryHost host,
            ILocalRepositoryModel sourceRepository, IRepositoryModel targetRepository,
            IBranch sourceBranch, IBranch targetBranch,
            string title, string body)
        {
            var repo = await Task.Run(() => gitService.GetRepository(sourceRepository.LocalPath));
            var remote = await gitClient.GetHttpRemote(repo, "origin");
            await gitClient.Push(repo, sourceBranch.Name, remote.Name);

            if (!repo.Branches[sourceBranch.Name].IsTracking)
                await gitClient.SetTrackingBranch(repo, sourceBranch.Name, remote.Name);

            // delay things a bit to avoid a race between pushing a new branch and creating a PR on it
            if (!Splat.ModeDetector.Current.InUnitTestRunner().GetValueOrDefault())
                await Task.Delay(TimeSpan.FromSeconds(5));

            var ret = await host.ModelService.CreatePullRequest(sourceRepository, targetRepository, sourceBranch, targetBranch, title, body);
            usageTracker.IncrementUpstreamPullRequestCount();
            return ret;
        }

        static string GetSafeBranchName(string name)
        {
            var before = InvalidBranchCharsRegex.Replace(name, "-");

            for (;;)
            {
                string after = before.Replace("--", "-");

                if (after == before)
                {
                    return before.ToLower(CultureInfo.InvariantCulture);
                }

                before = after;
            }
        }

        static string BranchNameToConfigKey(string name) => name.Replace("/", ".");

        static string ConfigKeyToBranchName(string name) => name.Replace(".", "/");
    }
}