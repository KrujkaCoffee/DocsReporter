using Microsoft.EntityFrameworkCore;
using DocsApi.Models;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace DocsApi.Data
{
    public class AppDbContext1 : DbContext
    {
        public AppDbContext1(DbContextOptions<AppDbContext1> options) : base(options) { }

        public DbSet<CodeErpDbResponse> ErpItems { get; set; }
        public DbSet<FileObjectId> FileObjectId { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CodeErpDbResponse>()
                .HasKey(e => e.s_ObjectID);
            modelBuilder.Entity<FileObjectId>()
                .HasKey(e => e.s_ObjectID);
        }
    }
    public class AppDbContext2 : DbContext
    {
        public AppDbContext2(DbContextOptions<AppDbContext2> options) : base(options) { }

        public DbSet<CodeErpDbResponse> ErpItems { get; set; }
        public DbSet<FileObjectId> FileObjectId { get; set; }
        public DbSet<ProccessTkpResponse> ProccessTkpObj { get; set; }
        public DbSet<StagesByDirResponse> StagesByDir { get; set; }
        public DbSet<TkpFoldersDBResponse> TkpFolders { get; set; }


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CodeErpDbResponse>()
                .HasKey(e => e.s_ObjectID);
            modelBuilder.Entity<FileObjectId>()
                .HasKey(e => e.s_ObjectID);
            modelBuilder.Entity<ProccessTkpResponse>()
                .HasKey(e => e.ID_proc);
            modelBuilder.Entity<StagesByDirResponse>()
                .HasKey(e => e.id);
            modelBuilder.Entity<TkpFoldersDBResponse>()
                .HasKey(e => e.id);
        }
    }
    public class AppDbContext3 : DbContext
    {
        public AppDbContext3(DbContextOptions<AppDbContext3> options) : base(options) { }

        public DbSet<CodeErpDbResponse> ErpItems { get; set; }
        public DbSet<FileObjectId> FileObjectId { get; set; }
        public DbSet<ProccessTkpResponse> ProccessTkpObj { get; set; }
        public DbSet<StagesByDirResponse> StagesByDir { get; set; }
        public DbSet<TkpFoldersDBResponse> TkpFolders { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CodeErpDbResponse>()
                .HasKey(e => e.s_ObjectID);
            modelBuilder.Entity<FileObjectId>()
                .HasKey(e => e.s_ObjectID);
            modelBuilder.Entity<ProccessTkpResponse>()
                .HasKey(e => e.ID_proc);
            modelBuilder.Entity<StagesByDirResponse>()
                .HasKey(e => e.id);
            modelBuilder.Entity<TkpFoldersDBResponse>()
                .HasKey(e => e.id);
        }
    }
}
