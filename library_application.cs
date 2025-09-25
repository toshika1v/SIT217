using System;
using System.Collections.Generic;
using System.Linq;

namespace LibraryDemo
{
    // --- Domain Models ---

    public class Book
    {
        public string Isbn { get; set; } = "";
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Genre { get; set; } = "";
        public bool Damaged { get; set; }
        public bool Lost { get; set; }
    }

    public class Member
    {
        public string MemberId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public string Contact { get; set; } = "";
        public string Country { get; set; } = "";
        public string Password { get; set; } = "";
        public List<string> BorrowedIsbns { get; } = new();

        public bool CanBorrow() => BorrowedIsbns.Count < 2; 
    }

    public class Staff
    {
        public string StaffId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Password { get; set; } = ""; // (plaintext)
    }

    public class Loan
    {
        public string Isbn { get; set; } = "";
        public string MemberId { get; set; } = "";
        public DateTime DueDate { get; set; }
    }

    public class Reservation
    {
        public string Isbn { get; set; } = "";
        public string MemberId { get; set; } = "";
        public DateTime CreatedOn { get; set; } = DateTime.Today;
    }

    public class SearchResult
    {
        public string Isbn { get; set; } = "";
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Genre { get; set; } = "";
        public string Availability { get; set; } = "";
    }


    // --- Coordinating / Service Layer ---

    public class Library
    {
        private readonly Dictionary<string, Book> _books = new();      // isbn -> Book
        private readonly Dictionary<string, Member> _members = new();  // memberId -> Member
        private readonly Dictionary<string, Staff> _staff = new();     // staffId -> Staff
        private readonly Dictionary<string, Loan> _loans = new();      // isbn -> Loan
        private readonly Dictionary<string, List<Reservation>> _reservations = new(); // isbn -> queue

        // --- Authentication ---
        public bool AuthenticateMember(string memberId, string password)
            => _members.TryGetValue(memberId, out var m) && m.Password == password;

        public bool AuthenticateStaff(string staffId, string password)
            => _staff.TryGetValue(staffId, out var s) && s.Password == password;

        // --- Registration / Admin ---
        public void RegisterMember(Member member)
        {
            if (!string.Equals(member.Country, "Australia", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only Australian residents can become members.");
            if (_members.ContainsKey(member.MemberId))
                throw new InvalidOperationException("Member ID already exists.");
            _members[member.MemberId] = member;
        }

        public void AddStaff(Staff staff)
        {
            if (_staff.ContainsKey(staff.StaffId))
                throw new InvalidOperationException("Staff ID already exists.");
            _staff[staff.StaffId] = staff;
        }

        public void AddBook(Book book)
        {
            if (_books.ContainsKey(book.Isbn))
                throw new InvalidOperationException("ISBN already exists.");
            _books[book.Isbn] = book;
        }

        public void UpdateBookInfo(string isbn, string? title = null, string? author = null, string? genre = null)
        {
            var book = RequireBook(isbn);
            if (title is not null) book.Title = title;
            if (author is not null) book.Author = author;
            if (genre is not null) book.Genre = genre;
        }

        public void MarkDamaged(string isbn)
        {
            var book = RequireBook(isbn);
            book.Damaged = true;
        }

        public void MarkLost(string isbn)
        {
            var book = RequireBook(isbn);
            book.Lost = true;
            if (_loans.ContainsKey(isbn))
                EndLoan(isbn);
        }

        // --- Search ---
        public List<SearchResult> SearchBooks(string? title = null, string? author = null, string? genre = null, string? isbn = null)
        {
            var results = new List<SearchResult>();
            foreach (var b in _books.Values)
            {
                if (isbn is not null && b.Isbn != isbn) continue;
                if (title is not null && !b.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) continue;
                if (author is not null && !b.Author.Contains(author, StringComparison.OrdinalIgnoreCase)) continue;
                if (genre is not null && !b.Genre.Contains(genre, StringComparison.OrdinalIgnoreCase)) continue;

                results.Add(new SearchResult
                {
                    Isbn = b.Isbn,
                    Title = b.Title,
                    Author = b.Author,
                    Genre = b.Genre,
                    Availability = GetAvailability(b.Isbn)
                });
            }
            return results;
        }

        public string GetAvailability(string isbn)
        {
            if (!_books.TryGetValue(isbn, out var book))
                return "unknown";
            if (book.Lost) return "lost";
            if (book.Damaged) return "damaged";
            if (_loans.ContainsKey(isbn))
                return _reservations.ContainsKey(isbn) && _reservations[isbn].Count > 0
                    ? "on loan (reserved)"
                    : "on loan";
            if (_reservations.ContainsKey(isbn) && _reservations[isbn].Count > 0)
                return "reserved";
            return "available";
        }

        // --- Borrow / Return / Reserve ---
        public Loan BorrowBook(string memberId, string isbn, int days = 14)
        {
            var member = RequireMember(memberId);
            var book = RequireBook(isbn);

            if (book.Damaged || book.Lost)
                throw new InvalidOperationException("Book is not available (damaged/lost).");
            if (!member.CanBorrow())
                throw new InvalidOperationException("Member has reached the 2-book borrowing limit.");
            if (_loans.ContainsKey(isbn))
                throw new InvalidOperationException("Book is already on loan.");

            var queue = _reservations.TryGetValue(isbn, out var rlist) ? rlist : new List<Reservation>();
            if (queue.Count > 0 && queue[0].MemberId != memberId)
                throw new InvalidOperationException("Book is reserved by another member. Please make a reservation.");

            var loan = new Loan
            {
                Isbn = isbn,
                MemberId = memberId,
                DueDate = DateTime.Today.AddDays(days)
            };
            _loans[isbn] = loan;
            member.BorrowedIsbns.Add(isbn);

            if (queue.Count > 0 && queue[0].MemberId == memberId)
                queue.RemoveAt(0);

            return loan;
        }

        public void ReturnBook(string memberId, string isbn)
        {
            var member = RequireMember(memberId);
            if (!_loans.TryGetValue(isbn, out var loan))
                throw new InvalidOperationException("This book is not currently on loan.");
            if (!string.Equals(loan.MemberId, memberId, StringComparison.Ordinal))
                throw new InvalidOperationException("This loan doesn't belong to the member.");

            EndLoan(isbn);
            member.BorrowedIsbns.Remove(isbn);
            NotifyNextReserver(isbn);
        }

        public Reservation ReserveBook(string memberId, string isbn)
        {
            RequireMember(memberId);
            RequireBook(isbn);

            if (!_reservations.ContainsKey(isbn))
                _reservations[isbn] = new List<Reservation>();

            var queue = _reservations[isbn];
            if (queue.Any(r => r.MemberId == memberId))
                throw new InvalidOperationException("Member already has a reservation for this book.");

            var res = new Reservation { Isbn = isbn, MemberId = memberId, CreatedOn = DateTime.Today };
            queue.Add(res);
            return res;
        }

        public void CancelReservation(string memberId, string isbn)
        {
            RequireMember(memberId);
            if (!_reservations.ContainsKey(isbn)) return;
            _reservations[isbn] = _reservations[isbn].Where(r => r.MemberId != memberId).ToList();
        }

        // --- Reporting / Queries ---
        public List<Loan> ListMemberLoans(string memberId)
        {
            RequireMember(memberId);
            return _loans.Values.Where(l => l.MemberId == memberId).ToList();
        }

        public List<Loan> OverdueLoans(DateTime? onDate = null)
        {
            var d = onDate ?? DateTime.Today;
            return _loans.Values.Where(l => l.DueDate < d).ToList();
        }

        // --- Internal helpers ---
        private Book RequireBook(string isbn)
        {
            if (!_books.TryGetValue(isbn, out var book))
                throw new InvalidOperationException("Book not found.");
            return book;
        }

        private Member RequireMember(string memberId)
        {
            if (!_members.TryGetValue(memberId, out var m))
                throw new InvalidOperationException("Member not found.");
            return m;
        }

        private void EndLoan(string isbn)
        {
            if (_loans.ContainsKey(isbn))
                _loans.Remove(isbn);
        }

        private string? NotifyNextReserver(string isbn)
        {
            if (_reservations.TryGetValue(isbn, out var queue) && queue.Count > 0)
            {
                var nextMemberId = queue[0].MemberId;
                var msg = $"Notification: Book {isbn} is now available for member {nextMemberId}.";
                Console.WriteLine(msg);
                return msg;
            }
            return null;
        }
    }


    // --- Demo (Program Main) ---

    public static class Program
    {
        public static void Main()
        {
            var lib = new Library();

            // Staff adds books
            lib.AddBook(new Book { Isbn = "111", Title = "Code", Author = "Robert C. Martin", Genre = "Software" });
            lib.AddBook(new Book { Isbn = "222", Title = "The Pragmatic Programmer", Author = "Andrew Hunt", Genre = "Software" });

            // Register members (Australian residents only)
            lib.RegisterMember(new Member
            {
                MemberId = "m1",
                Name = "Alice",
                Address = "1 Main St, Melbourne",
                Contact = "alice@example.com",
                Country = "Australia",
                Password = "pw1"
            });

            lib.RegisterMember(new Member
            {
                MemberId = "m2",
                Name = "Bob",
                Address = "2 King St, Sydney",
                Contact = "bob@example.com",
                Country = "Australia",
                Password = "pw2"
            });

            // Search example
            var found = lib.SearchBooks(author: "Martin");
            Console.WriteLine($"Search results: {found.Count}");
            foreach (var r in found)
            {
                Console.WriteLine($"{r.Title} by {r.Author} [{r.Isbn}] - {r.Availability}");
            }

            // Borrowing
            var loan = lib.BorrowBook("m1", "111", days: 7);
            Console.WriteLine($"{loan.MemberId} borrowed {loan.Isbn}, due {loan.DueDate:yyyy-MM-dd}");

            // Reservation while on loan
            var res = lib.ReserveBook("m2", "111");
            Console.WriteLine($"{res.MemberId} reserved {res.Isbn}");

            // Return triggers notification
            lib.ReturnBook("m1", "111");
        }
    }
}
