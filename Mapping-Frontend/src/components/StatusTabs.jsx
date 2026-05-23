export default function StatusTabs({ status, onChange }) {
  const tabs = ["pending", "approved", "rejected"];
  return (
    <div className="tabs">
      {tabs.map(t => (
        <button
          key={t}
          className={status === t ? "active" : ""}
          onClick={() => onChange(t)}
        >
          {t.toUpperCase()}
        </button>
      ))}
    </div>
  );
}