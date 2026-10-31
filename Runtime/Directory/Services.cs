namespace Fomoxa.Rpc.Directory
{
    public static class DirectoryRoles
    {
        public const string Backend = "backend";
        public const string Pod = "pod";
        public const string Gateway = "gateway";
        public const string Admin = "admin";
    }

    [Service("DirectoryAuthentication")]
    public partial interface IDirectoryAuthentication
    {
        [Unary]
        void Authenticate(DirectoryAuth request);
    }

    [Service("Directory")]
    public partial interface IDirectory
    {
        [Unary(Idempotent = true, Requires = DirectoryRoles.Backend, Details = new[] { typeof(NoPodAvailable) })]
        AllocateResponse Allocate(CallContext context, AllocateRequest request);

        [Unary(Requires = DirectoryRoles.Pod, Details = new[] { typeof(NoPodAvailable) })]
        AllocateResponse Relocate(CallContext context, RelocateRoom request);

        [Unary(Idempotent = true, Requires = DirectoryRoles.Pod)]
        PodRegistered RegisterPod(CallContext context, RegisterPod request);

        [Unary(Requires = DirectoryRoles.Pod)]
        void ReportLoad(CallContext context, PodLoad request);

        [Unary(Idempotent = true, Requires = DirectoryRoles.Pod)]
        void AcquireRoom(CallContext context, AcquireRoom request);

        [Unary(Idempotent = true, Requires = DirectoryRoles.Pod)]
        void ReleaseRoom(CallContext context, ReleaseRoom request);

        [ServerStream(Requires = DirectoryRoles.Pod)]
        void WatchDrain(ItemStream<DrainOrder> stream, DrainWatch request);

        [Unary(Requires = DirectoryRoles.Pod)]
        void RequestDrain(CallContext context, DrainSelf request);

        [ServerStream(Requires = DirectoryRoles.Gateway)]
        void WatchPods(ItemStream<PodChange> stream, PodWatch request);
    }

    [Service("DirectoryAdmin")]
    public partial interface IDirectoryAdmin
    {
        [Unary(Requires = DirectoryRoles.Admin)]
        void Drain(CallContext context, DrainPod request);

        [Unary(Requires = DirectoryRoles.Admin)]
        PodList ReadPods(CallContext context, ReadPods request);
    }
}
