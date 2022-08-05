﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Relecloud.Web.Api.Infrastructure;
using Relecloud.Web.Api.Services.TicketManagementService;
using Relecloud.Web.Models.ConcertContext;
using System.Text.Json;

namespace Relecloud.Web.Api.Services.SqlDatabaseConcertRepository
{
    public class SqlDatabaseConcertRepository : IConcertRepository, IDisposable
    {
        private readonly ConcertDataContext database;
        private readonly IDistributedCache cache;
        private readonly ITicketNumberGenerator ticketNumberGenerator;

        public SqlDatabaseConcertRepository(ConcertDataContext database, IDistributedCache cache, ITicketNumberGenerator ticketNumberGenerator)
        {
            this.database = database;
            this.cache = cache;
            this.ticketNumberGenerator = ticketNumberGenerator;
        }

        public void Initialize()
        {
            this.database.Initialize();
        }

        public async Task<CreateResult> CreateConcertAsync(Concert newConcert)
        {
            database.Add(newConcert);
            await this.database.SaveChangesAsync();
            this.cache.Remove(CacheKeys.UpcomingConcerts);
            return CreateResult.SuccessResult(newConcert.Id);
        }

        public async Task<UpdateResult> UpdateConcertAsync(Concert existingConcert)
        {
            database.Update(existingConcert);
            await database.SaveChangesAsync();
            this.cache.Remove(CacheKeys.UpcomingConcerts);
            return UpdateResult.SuccessResult();
        }

        public async Task<DeleteResult> DeleteConcertAsync(int concertId)
        {
            var existingConcert = this.database.Concerts.SingleOrDefault(c => c.Id == concertId);
            if (existingConcert != null)
            {
                database.Remove(existingConcert);
                await database.SaveChangesAsync();
                this.cache.Remove(CacheKeys.UpcomingConcerts);
            }

            return DeleteResult.SuccessResult();
        }

        public async Task<Concert?> GetConcertByIdAsync(int id)
        {
            return await this.database.Concerts.AsNoTracking().Where(c => c.Id == id).SingleOrDefaultAsync();
        }

        public async Task<ICollection<Concert>> GetConcertsByIdAsync(ICollection<int> ids)
        {
            return await this.database.Concerts.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync();
        }

        public async Task<ICollection<Concert>> GetUpcomingConcertsAsync(int count)
        {
            IList<Concert>? concerts;
            var concertsJson = await this.cache.GetStringAsync(CacheKeys.UpcomingConcerts);
            if (concertsJson != null)
            {
                // We have cached data, deserialize the JSON data.
                concerts = JsonSerializer.Deserialize<IList<Concert>>(concertsJson);
            }
            else
            {
                // There's nothing in the cache, retrieve data from the repository and cache it for one hour.
                concerts = await this.database.Concerts.AsNoTracking()
                    .Where(c => c.StartTime > DateTimeOffset.UtcNow && c.IsVisible)
                    .OrderBy(c => c.StartTime)
                    .Take(count)
                    .ToListAsync();
                concertsJson = JsonSerializer.Serialize(concerts);
                var cacheOptions = new DistributedCacheEntryOptions {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
                };
                await this.cache.SetStringAsync(CacheKeys.UpcomingConcerts, concertsJson, cacheOptions);
            }
            return concerts ?? new List<Concert>();
        }

        public async Task<UpdateResult> CreateOrUpdateUserAsync(User user)
        {
            var dbUser = await this.database.Users.FindAsync(user.Id);
            if (dbUser == null)
            {
                dbUser = new User { Id = user.Id };
                this.database.Users.Add(dbUser);
            }
            dbUser.DisplayName = user.DisplayName;
            await this.database.SaveChangesAsync();

            return UpdateResult.SuccessResult();
        }

        public async Task<int> GetCountForAllTicketsAsync(string userId)
        {
            return await this.database.Tickets.CountAsync(t => t.UserId == userId);
        }

        public async Task<PagedResult<Ticket>> GetAllTicketsAsync(string userId, int skip, int take)
        {
            var pageOfData = await this.database.Tickets.AsNoTracking().Include(t => t.Concert).Where(t => t.UserId == userId)
                .OrderByDescending(t=> t.Id).Skip(skip).Take(take).ToListAsync();
            var totalCount = await this.database.Tickets.Where(t => t.UserId == userId).CountAsync();

            return new PagedResult<Ticket>(pageOfData, totalCount);
        }

        public void Dispose()
        {
            if (this.database != null)
            {
                this.database.Dispose();
            }
        }

        public async Task<Ticket?> GetTicketByIdAsync(int id)
        {
            return await this.database.Tickets.AsNoTracking().Where(t => t.Id == id).SingleOrDefaultAsync();
        }

        public async Task<User?> GetUserByIdAsync(string id)
        {
            return await this.database.Users.AsNoTracking().Where(u => u.Id == id).SingleOrDefaultAsync();
        }

        public async Task<UpdateResult> CreateOrUpdateTicketNumbersAsync(int concertId, int numberOfTickets)
        {
            var strategy = this.database.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await this.database.Database.BeginTransactionAsync();

                    var existingTicketCount = await this.database.TicketNumbers.CountAsync(tn => tn.ConcertId == concertId);

                    if (existingTicketCount > numberOfTickets)
                    {
                        // we neeed to delete some tickets that haven't been sold
                        var unsoldTickets = await this.database.TicketNumbers.Where(tn => !tn.TicketId.HasValue).ToListAsync();
                        var ticketsToDelete = existingTicketCount - numberOfTickets;

                        if (unsoldTickets.Count() < ticketsToDelete)
                        {
                            throw new InvalidOperationException("Unable to delete sold tickets");
                        }

                        for (int i = 0; i < ticketsToDelete; i++)
                        {
                            var ticket = unsoldTickets[i];
                            this.database.TicketNumbers.Remove(ticket);
                        }
                    }
                    else if (existingTicketCount < numberOfTickets)
                    {
                        // we need to add n-many tickets
                        var ticketsToAdd = numberOfTickets - existingTicketCount;
                        for (int i = 0; i < ticketsToAdd; i++)
                        {
                            var newTicketNumber = this.ticketNumberGenerator.Generate();
                            this.database.TicketNumbers.Add(new TicketNumber
                            {
                                ConcertId = concertId,
                                Number = newTicketNumber
                            });
                        }
                    }

                    await this.database.SaveChangesAsync();
                    await transaction.CommitAsync();
                });

            return UpdateResult.SuccessResult();
        }
    }
}