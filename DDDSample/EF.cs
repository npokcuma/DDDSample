using System.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Book.Chapter10.EF
{
    public class CrmContext : DbContext
    {
        public CrmContext(DbContextOptions<CrmContext> options)
            : base(options)
        {
        }

        public CrmContext(string connectionString)
            : base (new DbContextOptionsBuilder<CrmContext>().UseSqlServer(connectionString).Options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Company> Companies { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>(x =>
            {
                x.ToTable("User").HasKey(k => k.UserId);
                x.Property(k => k.Email);
                x.Property(k => k.Type);
                x.Property(k => k.IsEmailConfirmed);
                x.Ignore(k => k.DomainEvents);
            });

            modelBuilder.Entity<Company>(x =>
            {
                x.ToTable("Company").HasKey(k => k.DomainName);
                x.Property(p => p.DomainName);
                x.Property(p => p.NumberOfEmployees);
            });
        }
    }

    public class User
    {
        public int UserId { get; set; }
        public string Email { get; private set; }
        public UserType Type { get; private set; }
        public bool IsEmailConfirmed { get; }
        public List<IDomainEvent> DomainEvents { get; }

        public User(int userId, string email, UserType type, bool isEmailConfirmed)
        {
            UserId = userId;
            Email = email;
            Type = type;
            IsEmailConfirmed = isEmailConfirmed;
            DomainEvents = new List<IDomainEvent>();
        }

        public void Save()
        {
            AddDomainEvent(new SaveUserEvent(this));
        }

        public string CanChangeEmail()
        {
            if (IsEmailConfirmed)
                return "Can't change email after it's confirmed";

            return null;
        }

        public void ChangeEmail(string newEmail, Company company)
        {
            Precondition.Requires(CanChangeEmail() == null);

            if (Email == newEmail)
                return;

            UserType newType = company.IsEmailCorporate(newEmail)
                ? UserType.Employee
                : UserType.Customer;

            if (Type != newType)
            {
                int delta = newType == UserType.Employee ? 1 : -1;
                company.ChangeNumberOfEmployees(delta);
                AddDomainEvent(new UserTypeChangedEvent(UserId, Type, newType));
            }

            Email = newEmail;
            Type = newType;
            AddDomainEvent(new EmailChangedEvent(UserId, newEmail));
        }

        private void AddDomainEvent(IDomainEvent domainEvent)
        {
            DomainEvents.Add(domainEvent);
        }
    }

    public class UserController
    {
        private readonly CrmContext _context;
        private readonly UserRepository _userRepository;
        private readonly CompanyRepository _companyRepository;
        private readonly EventDispatcher _eventDispatcher;

        public UserController(
            CrmContext context,
            MessageBus messageBus,
            IDomainLogger domainLogger)
        {
            _context = context;
            _userRepository = new UserRepository(context);
            _companyRepository = new CompanyRepository(context);
            _eventDispatcher = new EventDispatcher(
                messageBus, domainLogger);
        }
        
        public class UserDto
        {
            public int UserId { get; set; }
            public string Email { get; set; }
            public UserType Type { get; set; }
            public bool IsEmailConfirmed { get; set; }
        }
        
        public string SaveUser(UserDto dto)
        {
            User user = new User(dto.UserId, dto.Email, dto.Type, dto.IsEmailConfirmed);
            user.Save();
            _eventDispatcher.Dispatch(user.DomainEvents);
            _context.SaveChanges();
            
            return "OK";
        }

        public string ChangeEmail(int userId, string newEmail)
        {
            User user = _userRepository.GetUserById(userId);

            string error = user.CanChangeEmail();
            if (error != null)
                return error;

            Company company = _companyRepository.GetCompany();

            user.ChangeEmail(newEmail, company);

            _companyRepository.SaveCompany(company);
            _userRepository.SaveUser(user);
            _eventDispatcher.Dispatch(user.DomainEvents);

            _context.SaveChanges();
            return "OK";
        }
    }

    public class EventDispatcher
    {
        private readonly MessageBus _messageBus;
        private readonly IDomainLogger _domainLogger;

        public EventDispatcher(
            MessageBus messageBus,
            IDomainLogger domainLogger)
        {
            _domainLogger = domainLogger;
            _messageBus = messageBus;
        }

        public void Dispatch(List<IDomainEvent> events)
        {
            foreach (IDomainEvent ev in events)
            {
                Dispatch(ev);
            }
        }

        private void Dispatch(IDomainEvent ev)
        {
            switch (ev)
            {
                case SaveUserEvent saveUserEvent:
                    _messageBus.SaveUserCommand(saveUserEvent.User);
                    break;
                
                case EmailChangedEvent emailChangedEvent:
                    _messageBus.SendEmailChangedMessage(
                        emailChangedEvent.UserId,
                        emailChangedEvent.NewEmail);
                    break;

                case UserTypeChangedEvent userTypeChangedEvent:
                    _domainLogger.UserTypeHasChanged(
                        userTypeChangedEvent.UserId,
                        userTypeChangedEvent.OldType,
                        userTypeChangedEvent.NewType);
                    break;
            }
        }
    }

    public interface IDomainLogger
    {
        void UserTypeHasChanged(int userId, UserType oldType, UserType newType);
    }

    public class DomainLogger : IDomainLogger
    {
        private readonly ILogger _logger;

        public DomainLogger(ILogger logger)
        {
            _logger = logger;
        }

        public void UserTypeHasChanged(
            int userId, UserType oldType, UserType newType)
        {
            _logger.Info(
                $"User {userId} changed type " +
                $"from {oldType} to {newType}");
        }
    }

    public interface ILogger
    {
        void Info(string s);
    }

    public class UserTypeChangedEvent : IDomainEvent
    {
        public int UserId { get; }
        public UserType OldType { get; }
        public UserType NewType { get; }

        public UserTypeChangedEvent(int userId, UserType oldType, UserType newType)
        {
            UserId = userId;
            OldType = oldType;
            NewType = newType;
        }

        protected bool Equals(UserTypeChangedEvent other)
        {
            return UserId == other.UserId && string.Equals(OldType, other.OldType);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj.GetType() != this.GetType())
            {
                return false;
            }

            return Equals((EmailChangedEvent)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (UserId * 397) ^ OldType.GetHashCode();
            }
        }
    }

    public class SaveUserEvent : IDomainEvent
    {
        public User User { get; }

        /// <summary>
        /// Инициализирует новый экземпляр класса <see cref="SaveUserEvent"/>
        /// </summary>
        public SaveUserEvent(User user)
        {
            User = user;
        }
    }

    public class EmailChangedEvent : IDomainEvent
    {
        public int UserId { get; }
        public string NewEmail { get; }

        public EmailChangedEvent(int userId, string newEmail)
        {
            UserId = userId;
            NewEmail = newEmail;
        }

        protected bool Equals(EmailChangedEvent other)
        {
            return UserId == other.UserId && string.Equals(NewEmail, other.NewEmail);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj.GetType() != this.GetType())
            {
                return false;
            }

            return Equals((EmailChangedEvent)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (UserId * 397) ^ (NewEmail != null ? NewEmail.GetHashCode() : 0);
            }
        }
    }

    public interface IDomainEvent
    {
    }

    public class Company
    {
        public string DomainName { get; }
        public int NumberOfEmployees { get; private set; }

        public Company(string domainName, int numberOfEmployees)
        {
            DomainName = domainName;
            NumberOfEmployees = numberOfEmployees;
        }

        public void ChangeNumberOfEmployees(int delta)
        {
            Precondition.Requires(NumberOfEmployees + delta >= 0);

            NumberOfEmployees += delta;
        }

        public bool IsEmailCorporate(string email)
        {
            string emailDomain = email.Split('@')[1];
            return emailDomain == DomainName;
        }
    }

    public enum UserType
    {
        Customer = 1,
        Employee = 2
    }

    public static class Precondition
    {
        public static void Requires(bool precondition, string message = null)
        {
            if (precondition == false)
                throw new Exception(message);
        }
    }

    public class UserRepository
    {
        private readonly CrmContext _context;

        public UserRepository(CrmContext context)
        {
            _context = context;
        }

        public User GetUserById(int userId)
        {
            return _context.Users
                .SingleOrDefault(x => x.UserId == userId);
        }

        public void SaveUser(User user)
        {
            _context.Users.Update(user);
        }
    }

    public class CompanyRepository
    {
        private readonly CrmContext _context;

        public CompanyRepository(CrmContext context)
        {
            _context = context;
        }

        public Company GetCompany()
        {
            return _context.Companies
                .SingleOrDefault();
        }

        public void SaveCompany(Company company)
        {
            _context.Companies.Update(company);
        }

        public void AddCompany(Company company)
        {
            _context.Companies.Add(company);
        }
    }

    public class Transaction : IDisposable
    {
        private readonly TransactionScope _transaction;
        public readonly string ConnectionString;

        public Transaction(string connectionString)
        {
            _transaction = new TransactionScope();
            ConnectionString = connectionString;
        }

        public void Commit()
        {
            _transaction.Complete();
        }

        public void Dispose()
        {
            _transaction?.Dispose();
        }
    }

    public class MessageBus
    {
        private readonly IBus _bus;
        private readonly UserRepository _userRepository;

        public MessageBus(IBus bus, UserRepository userRepository)
        {
            _bus = bus;
            _userRepository = userRepository;
        }

        public void SendEmailChangedMessage(int userId, string newEmail)
        {
            _bus.Send("Type: USER EMAIL CHANGED; " +
                $"Id: {userId}; " +
                $"NewEmail: {newEmail}");
        }

        public void SaveUserCommand(User user)
        {
            _userRepository.SaveUser(user);
        }
    }

    public interface IBus
    {
        void Send(string message);
    }
}
