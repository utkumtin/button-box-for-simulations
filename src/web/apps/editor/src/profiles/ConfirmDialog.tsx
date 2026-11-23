export interface ConfirmRequest {
  title: string;
  message: string;
  confirmLabel: string;
  danger?: boolean;
  onConfirm: () => void;
}

/** In-page confirmation (the editor never uses window.confirm). Cancel is the first button so Enter does no harm. */
export function ConfirmDialog({ request, onCancel }: { request: ConfirmRequest; onCancel: () => void }) {
  return (
    <div className="overlay">
      <div className="dialog pe-confirm" role="dialog" aria-modal="true" aria-labelledby="pe-confirm-title">
        <h3 id="pe-confirm-title">{request.title}</h3>
        <p>{request.message}</p>
        <div className="row-actions">
          <button type="button" className="btn" autoFocus onClick={onCancel}>
            Cancel
          </button>
          <button
            type="button"
            className={request.danger ? 'btn btn-danger' : 'btn btn-primary'}
            onClick={() => {
              request.onConfirm();
            }}
          >
            {request.confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
