export default function CompetenceTable({ items, onSelect, onApprove, onReject, onAssignOther }) {
  return (
    <table>
      <thead>
        <tr>
          <th>Name</th>
          <th>Area</th>
          <th>Category</th>
          <th>Status</th>
          <th>Confidence</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        {items.map(c => (
          <tr key={c.competenceId}>
            <td>
              <button onClick={() => onSelect(c)}>{c.name}</button>
            </td>
            <td>{c.areaName}</td>
            <td>{c.categoryName}</td>
            <td>{c.status}</td>
            <td>{c.confidence?.toFixed?.(2)}</td>
            <td>
              {onApprove && <button onClick={() => onApprove(c)}>Approve</button>}
              {onReject && <button onClick={() => onReject(c)}>Reject</button>}
              {onAssignOther && <button onClick={() => onAssignOther(c)}>Other</button>}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}