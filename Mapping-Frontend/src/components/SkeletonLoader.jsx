import "./SkeletonLoader.css";

export function SkeletonCard() {
  return (
    <div className="skeleton-card">
      <div className="skeleton-line skeleton-title"></div>
      <div className="skeleton-line skeleton-text"></div>
      <div className="skeleton-line skeleton-text short"></div>
      <div className="skeleton-line skeleton-text"></div>
    </div>
  );
}

export function SkeletonTable({ rows = 5, columns = 6 }) {
  return (
    <div className="skeleton-table">
      <div className="skeleton-table-header">
        {Array.from({ length: columns }).map((_, i) => (
          <div key={i} className="skeleton-line skeleton-header"></div>
        ))}
      </div>
      {Array.from({ length: rows }).map((_, rowIdx) => (
        <div key={rowIdx} className="skeleton-table-row">
          {Array.from({ length: columns }).map((_, colIdx) => (
            <div key={colIdx} className="skeleton-line skeleton-cell"></div>
          ))}
        </div>
      ))}
    </div>
  );
}

export function SkeletonText({ lines = 3, width = "100%" }) {
  return (
    <div className="skeleton-text-container" style={{ width }}>
      {Array.from({ length: lines }).map((_, i) => (
        <div
          key={i}
          className={`skeleton-line skeleton-text ${i === lines - 1 ? "short" : ""}`}
        ></div>
      ))}
    </div>
  );
}

export function SkeletonButton() {
  return <div className="skeleton-button skeleton-line"></div>;
}