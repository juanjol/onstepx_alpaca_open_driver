using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using ASCOM;
using ASCOM.DeviceInterface;
using Library = ASCOM.Common.DeviceInterfaces;

namespace OnStepX.ComShim.Drivers
{
    /// <summary>
    /// One axis rate range, in the shape COM clients expect.
    /// </summary>
    /// <remarks>
    /// <c>ASCOM.DeviceInterfaces</c> declares <see cref="IRate"/> but ships no
    /// class that implements it, so the shim has to bring its own. These types
    /// are never created through COM, only returned from a telescope, which is
    /// why they have no ProgID and are not registered.
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("527a10a5-3964-42ef-bfb8-aa3738522cc0")]
    public sealed class ComRate : IRate
    {
        /// <summary>Creates a rate range.</summary>
        public ComRate(double minimum, double maximum)
        {
            Minimum = minimum;
            Maximum = maximum;
        }

        /// <inheritdoc />
        public double Minimum { get; set; }

        /// <inheritdoc />
        public double Maximum { get; set; }

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The rates at which a telescope axis can be moved, in the shape COM
    /// clients expect.
    /// </summary>
    /// <remarks>
    /// Indexing is one based, as the ASCOM COM interfaces have always been.
    /// The library side is copied by enumeration rather than by index because
    /// the Alpaca client's own collection doubles as its enumerator and shares
    /// a cursor between the two, so touching it twice is not safe.
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("539f0dda-5c5c-4a87-9ad8-073f239e72b8")]
    public sealed class ComAxisRates : IAxisRates, IEnumerable
    {
        private readonly IRate[] _rates;

        /// <summary>Copies the rates out of an Alpaca client collection.</summary>
        public ComAxisRates(Library.IAxisRates rates)
        {
            List<IRate> copied = new List<IRate>();

            if (rates != null)
            {
                foreach (Library.IRate rate in rates)
                {
                    copied.Add(new ComRate(rate.Minimum, rate.Maximum));
                }
            }

            _rates = copied.ToArray();
        }

        /// <inheritdoc />
        public int Count => _rates.Length;

        /// <inheritdoc />
        public IRate this[int index]
        {
            get
            {
                if (index < 1 || index > _rates.Length)
                {
                    throw new InvalidValueException(
                        "AxisRates.Item",
                        index.ToString(CultureInfo.InvariantCulture),
                        "1",
                        _rates.Length.ToString(CultureInfo.InvariantCulture));
                }

                return _rates[index - 1];
            }
        }

        /// <inheritdoc />
        public IEnumerator GetEnumerator()
        {
            return new ComCollectionEnumerator(_rates);
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The tracking rates a telescope offers, in the shape COM clients expect.
    /// </summary>
    /// <remarks>
    /// Same one based indexing and same reason for copying by enumeration as
    /// <see cref="ComAxisRates"/>, made worse here because the Alpaca client's
    /// tracking rate collection keeps its cursor in a static field.
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("5e289ea0-5e5d-41ce-8740-e9980e3523c2")]
    public sealed class ComTrackingRates : ITrackingRates, IEnumerable
    {
        private readonly DriveRates[] _rates;

        /// <summary>Copies the rates out of an Alpaca client collection.</summary>
        public ComTrackingRates(Library.ITrackingRates rates)
        {
            List<DriveRates> copied = new List<DriveRates>();

            if (rates != null)
            {
                foreach (Library.DriveRate rate in rates)
                {
                    copied.Add((DriveRates)rate);
                }
            }

            _rates = copied.ToArray();
        }

        /// <inheritdoc />
        public int Count => _rates.Length;

        /// <inheritdoc />
        public DriveRates this[int index]
        {
            get
            {
                if (index < 1 || index > _rates.Length)
                {
                    throw new InvalidValueException(
                        "TrackingRates.Item",
                        index.ToString(CultureInfo.InvariantCulture),
                        "1",
                        _rates.Length.ToString(CultureInfo.InvariantCulture));
                }

                return _rates[index - 1];
            }
        }

        /// <inheritdoc />
        public IEnumerator GetEnumerator()
        {
            object[] boxed = new object[_rates.Length];

            for (int index = 0; index < _rates.Length; index++)
            {
                boxed[index] = _rates[index];
            }

            return new ComCollectionEnumerator(boxed);
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Enumerator over a fixed snapshot, visible to COM.
    /// </summary>
    /// <remarks>
    /// The enumerator a plain array hands out is not COM visible, so a client
    /// walking the collection would get an object it cannot call. Snapshot
    /// based means two clients enumerating at once cannot disturb each other,
    /// which is exactly the flaw this shim is working around in the Alpaca
    /// client's own collections.
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("4320b56e-1976-4baa-ac81-321e50ab7182")]
    public sealed class ComCollectionEnumerator : IEnumerator
    {
        private readonly object[] _items;

        private int _position = -1;

        /// <summary>Creates an enumerator over the given items.</summary>
        public ComCollectionEnumerator(object[] items)
        {
            _items = items ?? new object[0];
        }

        /// <inheritdoc />
        public object Current
        {
            get
            {
                if (_position < 0 || _position >= _items.Length)
                {
                    throw new System.InvalidOperationException("The enumerator is not positioned on an element");
                }

                return _items[_position];
            }
        }

        /// <inheritdoc />
        public bool MoveNext()
        {
            _position++;
            return _position < _items.Length;
        }

        /// <inheritdoc />
        public void Reset()
        {
            _position = -1;
        }
    }
}
