using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<OrganizationEntity> Organizations => Set<OrganizationEntity>();

    public DbSet<OrganizationMemberEntity> OrganizationMembers => Set<OrganizationMemberEntity>();

    public DbSet<LiveRoomEntity> LiveRooms => Set<LiveRoomEntity>();

    public DbSet<ManagedDeviceEntity> Devices => Set<ManagedDeviceEntity>();

    public DbSet<DeviceEnrollmentEntity> DeviceEnrollments => Set<DeviceEnrollmentEntity>();

    public DbSet<DesktopAuthorizationSessionEntity> DesktopAuthorizationSessions => Set<DesktopAuthorizationSessionEntity>();

    public DbSet<DesktopAccessTokenEntity> DesktopAccessTokens => Set<DesktopAccessTokenEntity>();

    public DbSet<CurrentParameterStateEntity> CurrentParameterStates => Set<CurrentParameterStateEntity>();

    public DbSet<DeviceCapabilityEntity> DeviceCapabilities => Set<DeviceCapabilityEntity>();

    public DbSet<DeviceHeartbeatEntity> DeviceHeartbeats => Set<DeviceHeartbeatEntity>();

    public DbSet<SnapshotEntity> Snapshots => Set<SnapshotEntity>();

    public DbSet<SnapshotUploadEntity> SnapshotUploads => Set<SnapshotUploadEntity>();

    public DbSet<SnapshotComponentEntity> SnapshotComponents => Set<SnapshotComponentEntity>();

    public DbSet<AssetEntity> Assets => Set<AssetEntity>();

    public DbSet<SnapshotAssetEntity> SnapshotAssets => Set<SnapshotAssetEntity>();

    public DbSet<ObjectDeletionEntity> ObjectDeletions => Set<ObjectDeletionEntity>();

    public DbSet<DeviceMappingEntity> DeviceMappings => Set<DeviceMappingEntity>();

    public DbSet<RemoteJobEntity> RemoteJobs => Set<RemoteJobEntity>();

    public DbSet<JobEventEntity> JobEvents => Set<JobEventEntity>();

    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    public DbSet<AdapterCatalogEntity> AdapterCatalog => Set<AdapterCatalogEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<OrganizationMemberEntity>().HasKey(member => new { member.OrganizationId, member.UserId });
        builder.Entity<OrganizationMemberEntity>().Property(member => member.Role).HasConversion<string>();
        builder.Entity<OrganizationMemberEntity>()
            .HasOne<OrganizationEntity>()
            .WithMany()
            .HasForeignKey(member => member.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<LiveRoomEntity>()
            .HasOne<OrganizationEntity>()
            .WithMany()
            .HasForeignKey(room => room.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ManagedDeviceEntity>()
            .HasOne<OrganizationEntity>()
            .WithMany()
            .HasForeignKey(device => device.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ManagedDeviceEntity>()
            .HasOne<LiveRoomEntity>()
            .WithMany()
            .HasForeignKey(device => device.RoomId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ManagedDeviceEntity>().HasIndex(device => new { device.OrganizationId, device.RoomId });
        builder.Entity<DeviceEnrollmentEntity>().HasIndex(enrollment => enrollment.TokenHash).IsUnique();
        builder.Entity<DeviceEnrollmentEntity>().Property(enrollment => enrollment.TokenHash).HasMaxLength(64);
        builder.Entity<DesktopAuthorizationSessionEntity>().HasIndex(session => session.UserCode).IsUnique();
        builder.Entity<DesktopAuthorizationSessionEntity>().HasIndex(session => session.DeviceCodeHash).IsUnique();
        builder.Entity<DesktopAuthorizationSessionEntity>().Property(session => session.DeviceCodeHash).HasMaxLength(32);
        builder.Entity<DesktopAccessTokenEntity>().HasIndex(token => token.TokenHash).IsUnique();
        builder.Entity<DesktopAccessTokenEntity>().Property(token => token.TokenHash).HasMaxLength(32);
        builder.Entity<DesktopAccessTokenEntity>()
            .HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ManagedDeviceEntity>().Property(device => device.DeviceKeyHash).HasMaxLength(64);
        builder.Entity<CurrentParameterStateEntity>().HasKey(state => state.DeviceId);
        builder.Entity<CurrentParameterStateEntity>().Property(state => state.ParametersJson).HasColumnType("jsonb");
        builder.Entity<DeviceCapabilityEntity>().HasKey(state => state.DeviceId);
        builder.Entity<DeviceCapabilityEntity>().Property(state => state.CapabilityJson).HasColumnType("jsonb");
        builder.Entity<DeviceHeartbeatEntity>().Property(state => state.ApplicationVersionsJson).HasColumnType("jsonb");
        builder.Entity<DeviceHeartbeatEntity>().HasIndex(state => new { state.OrganizationId, state.DeviceId, state.ObservedAt });
        builder.Entity<ManagedDeviceEntity>().Property(device => device.ApplicationVersionsJson).HasColumnType("jsonb");
        builder.Entity<ManagedDeviceEntity>().Property(device => device.CapabilitiesJson).HasColumnType("jsonb");
        builder.Entity<SnapshotEntity>().Property(snapshot => snapshot.ManifestJson).HasColumnType("jsonb");
        builder.Entity<SnapshotComponentEntity>().Property(component => component.Application).HasConversion<string>();
        builder.Entity<SnapshotComponentEntity>().Property(component => component.ParametersJson).HasColumnType("jsonb");
        builder.Entity<SnapshotComponentEntity>().HasIndex(component => new { component.SnapshotId, component.Application }).IsUnique();
        builder.Entity<SnapshotComponentEntity>()
            .HasOne<SnapshotEntity>()
            .WithMany()
            .HasForeignKey(component => component.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AssetEntity>().HasKey(asset => new { asset.OrganizationId, asset.Sha256 });
        builder.Entity<AssetEntity>().HasIndex(asset => asset.ObjectKey).IsUnique();
        builder.Entity<SnapshotAssetEntity>().HasKey(value => new { value.OrganizationId, value.SnapshotId, value.Sha256 });
        builder.Entity<SnapshotAssetEntity>()
            .HasOne<SnapshotEntity>()
            .WithMany()
            .HasForeignKey(value => value.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<SnapshotAssetEntity>()
            .HasOne<AssetEntity>()
            .WithMany()
            .HasForeignKey(value => new { value.OrganizationId, value.Sha256 })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ObjectDeletionEntity>().HasIndex(value => value.ObjectKey).IsUnique();
        builder.Entity<ObjectDeletionEntity>().HasIndex(value => value.NextAttemptAt);
        builder.Entity<RemoteJobEntity>().Property(job => job.Kind).HasConversion<string>();
        builder.Entity<RemoteJobEntity>().Property(job => job.Status).HasConversion<string>();
        builder.Entity<RemoteJobEntity>().Property(job => job.Compatibility).HasConversion<string>();
        builder.Entity<JobEventEntity>().Property(jobEvent => jobEvent.Status).HasConversion<string>();
        builder.Entity<DeviceMappingEntity>().Property(mapping => mapping.Application).HasConversion<string>();
        builder.Entity<RemoteJobEntity>().HasIndex(job => new { job.DeviceId, job.Status });
        builder.Entity<JobEventEntity>()
            .HasOne<RemoteJobEntity>()
            .WithMany()
            .HasForeignKey(jobEvent => jobEvent.JobId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<JobEventEntity>().HasIndex(jobEvent => new
        {
            jobEvent.JobId,
            jobEvent.ExecutionId,
            jobEvent.Sequence
        }).IsUnique();
        builder.Entity<SnapshotEntity>().HasIndex(snapshot => new { snapshot.OrganizationId, snapshot.RoomId, snapshot.CreatedAt });
        builder.Entity<SnapshotUploadEntity>().HasIndex(upload => new { upload.OrganizationId, upload.ExpiresAt });
        builder.Entity<AuditEventEntity>().HasIndex(audit => new { audit.OrganizationId, audit.OccurredAt });
        builder.Entity<AuditEventEntity>().Property(audit => audit.DetailJson).HasColumnType("jsonb");
        builder.Entity<AdapterCatalogEntity>().Property(adapter => adapter.Application).HasConversion<string>();
        builder.Entity<AdapterCatalogEntity>()
            .HasIndex(adapter => new { adapter.Application, adapter.StructureFingerprint })
            .IsUnique();
    }
}
