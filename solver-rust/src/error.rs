use thiserror::Error;

#[derive(Debug, Error)]
pub enum SolverError {
    #[error("schema error at {path}: {message}")]
    Schema { path: String, message: String },

    #[error("unsupported rules: {0}")]
    Unsupported(String),

    #[error("illegal action: {0}")]
    IllegalAction(String),

    #[error("search exceeded maximum_states={0}")]
    StateLimit(usize),

    #[error("search exceeded maximum_depth={0}")]
    DepthLimit(u8),

    #[error("search exceeded its time budget")]
    TimeLimit,

    #[error("search was cancelled")]
    Cancelled,

    #[error("terminal result conflicts with durable content for this game")]
    ResultObservationConflict,

    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),

    #[error("JSON error: {0}")]
    Json(#[from] serde_json::Error),

    #[error("HTTP server error: {0}")]
    Http(String),
}

impl SolverError {
    #[must_use]
    pub fn schema(path: impl Into<String>, message: impl Into<String>) -> Self {
        Self::Schema {
            path: path.into(),
            message: message.into(),
        }
    }

    #[must_use]
    pub fn code(&self) -> &'static str {
        match self {
            Self::Schema { .. } | Self::Json(_) => "schema_error",
            Self::Unsupported(_) => "unsupported_scope",
            Self::IllegalAction(_) => "illegal_action",
            Self::StateLimit(_) => "state_limit",
            Self::DepthLimit(_) => "depth_limit",
            Self::TimeLimit => "time_limit_reached",
            Self::Cancelled => "cancelled",
            Self::ResultObservationConflict => "result_observation_conflict",
            Self::Io(_) | Self::Http(_) => "internal_error",
        }
    }

    #[must_use]
    pub fn path(&self) -> &str {
        match self {
            Self::Schema { path, .. } => path,
            _ => "",
        }
    }

    #[must_use]
    pub fn public_message(&self) -> String {
        match self {
            Self::Schema { path, .. } => {
                if path.is_empty() {
                    "请求数据格式不正确。".to_owned()
                } else {
                    format!("请求数据格式不正确：{path}。")
                }
            }
            Self::Json(_) => "请求不是有效的 JSON 数据。".to_owned(),
            Self::Unsupported(_) => {
                "当前局面包含尚未支持或无法精确验证的规则，已停止给出可能误导的建议。".to_owned()
            }
            Self::IllegalAction(_) => "求解过程中发现不合法的动作序列。".to_owned(),
            Self::StateLimit(_) => "局面搜索量超过安全上限，请缩小局面或稍后重试。".to_owned(),
            Self::DepthLimit(_) => "局面搜索深度超过安全上限，未把截断结果标记为完整。".to_owned(),
            Self::TimeLimit => "局面搜索达到本次时间上限，未把截断结果标记为完整。".to_owned(),
            Self::Cancelled => "本次求解已取消。".to_owned(),
            Self::ResultObservationConflict => {
                "该对局已有不同的终局结果记录，已拒绝覆盖。".to_owned()
            }
            Self::Io(_) | Self::Http(_) => "求解器发生内部错误，请稍后重试。".to_owned(),
        }
    }
}
